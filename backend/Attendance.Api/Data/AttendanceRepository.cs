using System.Data;
using Attendance.Api.Domain;
using Attendance.Api.Time;
using Microsoft.Data.SqlClient;

namespace Attendance.Api.Data;

/// <summary>All database access. Parameterized SQL only.</summary>
public sealed class AttendanceRepository(string connectionString, IExternalTimeService time, TimeProvider clock)
{
    private const int HistoryLimit = 30;

    private const string ShiftSelect = """
        SELECT s.Id, s.EmployeeId, e.FullName, s.StartUtc, s.EndUtc, s.StartSource, s.EndSource
        FROM dbo.Shifts s JOIN dbo.Employees e ON e.Id = s.EmployeeId
        """;

    public async Task<IReadOnlyList<Employee>> GetEmployeesAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        return await ReadEmployeesAsync(connection, ct);
    }

    public async Task<EmployeeAttendance> GetAttendanceAsync(int employeeId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var employee = await GetEmployeeAsync(connection, null, employeeId, lockRow: false, ct);

        // The open shift is always fetched separately, so it appears even if it is older than the last 30 shifts.
        await using var command = new SqlCommand($"""
            {ShiftSelect} WHERE s.EmployeeId = @employee AND s.EndUtc IS NULL;
            SELECT TOP (@limit) x.* FROM ({ShiftSelect} WHERE s.EmployeeId = @employee) x ORDER BY x.StartUtc DESC;
            """, connection);
        command.Parameters.Add("@employee", SqlDbType.Int).Value = employeeId;
        command.Parameters.Add("@limit", SqlDbType.Int).Value = HistoryLimit;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var open = (await ReadShiftsAsync(reader, ct)).SingleOrDefault();
        await reader.NextResultAsync(ct);
        var history = await ReadShiftsAsync(reader, ct);

        return new EmployeeAttendance(
            employee,
            open is null ? null : ShiftDto.From(open),
            history.Select(ShiftDto.From).ToList(),
            clock.GetUtcNow());
    }

    public async Task<PunchReceipt?> FindPunchAsync(int employeeId, Guid requestId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await GetEmployeeAsync(connection, null, employeeId, lockRow: false, ct);
        return await FindReceiptAsync(connection, null, employeeId, requestId, ct);
    }

    /// <summary>
    /// Clock-in or clock-out. Order of operations:
    /// 1. Validate input and replay (the same requestId → the original receipt, with no call to the time service).
    /// 2. A quick lock-free precheck, so the external service is not called for a request that will be rejected anyway.
    /// 3. Fetch the time from the external service, <b>before</b> opening the transaction.
    /// 4. Transaction: lock the employee row (UPDLOCK, HOLDLOCK) → re-check → write the shift + punch together.
    /// </summary>
    public async Task<PunchReceipt> PunchAsync(int employeeId, string action, PunchRequest request, CancellationToken ct)
    {
        if (request.RequestId == Guid.Empty
            || (action == PunchActions.In && request.ShiftId is not null)
            || (action == PunchActions.Out && (request.ShiftId is null || request.ShiftId == Guid.Empty)))
            throw new AttendanceException(400, "invalid_punch", "פרטי ההחתמה אינם תקינים.");

        await using var connection = await OpenAsync(ct);
        await GetEmployeeAsync(connection, null, employeeId, lockRow: false, ct);

        var existing = await FindReceiptAsync(connection, null, employeeId, request.RequestId, ct);
        if (existing is not null) return Replay(existing, action, request.ShiftId);

        EnsureAllowed(action, request.ShiftId, await FindOpenShiftAsync(connection, null, employeeId, ct), at: null);

        var now = await time.GetCurrentAsync(ct);

        try
        {
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await GetEmployeeAsync(connection, transaction, employeeId, lockRow: true, ct);

            existing = await FindReceiptAsync(connection, transaction, employeeId, request.RequestId, ct);
            if (existing is not null)
            {
                await transaction.CommitAsync(ct);
                return Replay(existing, action, request.ShiftId);
            }

            var open = await FindOpenShiftAsync(connection, transaction, employeeId, ct);
            EnsureAllowed(action, request.ShiftId, open, now.UtcNow);
            if (action == PunchActions.In)
                EnsureAfterPreviousShift(await FindLastEndAsync(connection, transaction, employeeId, ct), now.UtcNow);

            var shiftId = open?.Id ?? Guid.NewGuid();
            var write = action == PunchActions.In
                ? "INSERT INTO dbo.Shifts (Id, EmployeeId, StartUtc, StartSource) VALUES (@shift, @employee, @at, @source);"
                : "UPDATE dbo.Shifts SET EndUtc = @at, EndSource = @source WHERE Id = @shift AND EmployeeId = @employee AND EndUtc IS NULL;";

            await using var command = new SqlCommand(write + """

                INSERT INTO dbo.Punches (EmployeeId, RequestId, ShiftId, Action, AtUtc, TimeSource, ZurichOffsetMinutes)
                VALUES (@employee, @request, @shift, @action, @at, @source, @offset);
                """, connection, transaction);
            command.Parameters.Add("@employee", SqlDbType.Int).Value = employeeId;
            command.Parameters.Add("@shift", SqlDbType.UniqueIdentifier).Value = shiftId;
            command.Parameters.Add("@request", SqlDbType.UniqueIdentifier).Value = request.RequestId;
            command.Parameters.Add("@action", SqlDbType.VarChar, 3).Value = action;
            command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = now.UtcNow;
            command.Parameters.Add("@source", SqlDbType.VarChar, 40).Value = now.Source;
            command.Parameters.Add("@offset", SqlDbType.SmallInt).Value = (short)ZurichTime.ToZurich(now.UtcNow).Offset.TotalMinutes;
            await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);

            return new PunchReceipt(request.RequestId, shiftId, action, now.UtcNow, ZurichTime.ToZurich(now.UtcNow), now.Source, Replayed: false);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // Last line of defense: a unique index caught a race that slipped past the lock (for example, the same requestId at the same moment).
            existing = await FindReceiptAsync(connection, null, employeeId, request.RequestId, ct);
            if (existing is not null) return Replay(existing, action, request.ShiftId);
            throw new AttendanceException(409, "concurrent_punch", "התקבלה החתמה מקבילה. רעננו את המסך ובדקו את המצב.");
        }
    }

    public async Task<AttendanceReport> GetReportAsync(ReportRange range, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var employees = await ReadEmployeesAsync(connection, ct);

        await using var command = new SqlCommand($"""
            {ShiftSelect}
            WHERE s.StartUtc < @end AND (s.EndUtc IS NULL OR s.EndUtc > @start)
            ORDER BY s.StartUtc, s.EmployeeId;
            """, connection);
        command.Parameters.Add("@start", SqlDbType.DateTimeOffset).Value = range.StartUtc;
        command.Parameters.Add("@end", SqlDbType.DateTimeOffset).Value = range.EndUtcExclusive;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var shifts = await ReadShiftsAsync(reader, ct);

        // The server clock is used here only to estimate the elapsed time of a shift that is still open (marked as open in the report).
        return ReportBuilder.Build(range, employees, shifts, clock.GetUtcNow());
    }

    private static void EnsureAllowed(string action, Guid? requestedShiftId, OpenShift? open, DateTimeOffset? at)
    {
        if (action == PunchActions.In && open is not null)
            throw new AttendanceException(409, "shift_already_open", "כבר יש משמרת פתוחה. רעננו את המסך כדי לראות אותה.");
        if (action == PunchActions.Out && open is null)
            throw new AttendanceException(409, "no_open_shift", "אין משמרת פתוחה לסגירה. ייתכן שהיא נסגרה בחלון אחר.");
        if (action == PunchActions.Out && open!.Id != requestedShiftId)
            throw new AttendanceException(409, "shift_changed", "המשמרת השתנתה מאז שהמסך נטען. רעננו ונסו שוב.");
        if (at is { } t && open is not null && t < open.StartUtc)
            throw new AttendanceException(409, "time_before_start",
                "השעה שהתקבלה משירות הזמן מוקדמת משעת הכניסה. ההחתמה לא נשמרה, נסו שוב.");
    }

    /// <summary>
    /// The providers' clocks can differ by a few seconds. If one closed the previous shift at 17:00:05 and another answers
    /// an immediate clock-in with 17:00:02, the new shift would start before the previous one ended and the overlap would be counted twice.
    /// Checked inside the transaction, under the employee-row lock, so no concurrent clock-out can slip in between.
    /// </summary>
    private static void EnsureAfterPreviousShift(DateTimeOffset? lastEndUtc, DateTimeOffset at)
    {
        if (lastEndUtc is { } end && at < end)
            throw new AttendanceException(409, "time_before_previous_end",
                "השעה שהתקבלה משירות הזמן מוקדמת מסיום המשמרת הקודמת. ההחתמה לא נשמרה, נסו שוב בעוד רגע.");
    }

    private static async Task<DateTimeOffset?> FindLastEndAsync(SqlConnection c, SqlTransaction t, int employeeId, CancellationToken ct)
    {
        await using var command = new SqlCommand("SELECT MAX(EndUtc) FROM dbo.Shifts WHERE EmployeeId = @employee;", c, t);
        command.Parameters.Add("@employee", SqlDbType.Int).Value = employeeId;
        return await command.ExecuteScalarAsync(ct) is DateTimeOffset end ? end : null;
    }

    private static PunchReceipt Replay(PunchReceipt receipt, string action, Guid? shiftId)
    {
        if (receipt.Action != action || (action == PunchActions.Out && receipt.ShiftId != shiftId))
            throw new AttendanceException(409, "request_reused", "מזהה ההחתמה כבר שימש לפעולה אחרת.");
        return receipt with { Replayed = true };
    }

    private sealed record OpenShift(Guid Id, DateTimeOffset StartUtc);

    private static async Task<OpenShift?> FindOpenShiftAsync(SqlConnection c, SqlTransaction? t, int employeeId, CancellationToken ct)
    {
        await using var command = new SqlCommand(
            "SELECT Id, StartUtc FROM dbo.Shifts WHERE EmployeeId = @employee AND EndUtc IS NULL;", c, t);
        command.Parameters.Add("@employee", SqlDbType.Int).Value = employeeId;
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new OpenShift(reader.GetGuid(0), reader.GetDateTimeOffset(1)) : null;
    }

    private static async Task<Employee> GetEmployeeAsync(SqlConnection c, SqlTransaction? t, int employeeId, bool lockRow, CancellationToken ct)
    {
        // UPDLOCK + HOLDLOCK on the employee row serializes punches for the same employee, even across multiple server instances.
        var hint = lockRow ? " WITH (UPDLOCK, HOLDLOCK)" : "";
        await using var command = new SqlCommand(
            $"SELECT Id, FullName FROM dbo.Employees{hint} WHERE Id = @employee AND IsActive = 1;", c, t);
        command.Parameters.Add("@employee", SqlDbType.Int).Value = employeeId;
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new Employee(reader.GetInt32(0), reader.GetString(1))
            : throw new AttendanceException(404, "employee_not_found", "העובד לא נמצא.");
    }

    private static async Task<PunchReceipt?> FindReceiptAsync(SqlConnection c, SqlTransaction? t, int employeeId, Guid requestId, CancellationToken ct)
    {
        await using var command = new SqlCommand(
            "SELECT ShiftId, Action, AtUtc, TimeSource FROM dbo.Punches WHERE EmployeeId = @employee AND RequestId = @request;", c, t);
        command.Parameters.Add("@employee", SqlDbType.Int).Value = employeeId;
        command.Parameters.Add("@request", SqlDbType.UniqueIdentifier).Value = requestId;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var at = reader.GetDateTimeOffset(2);
        return new PunchReceipt(requestId, reader.GetGuid(0), reader.GetString(1), at, ZurichTime.ToZurich(at), reader.GetString(3), Replayed: false);
    }

    private static async Task<IReadOnlyList<Employee>> ReadEmployeesAsync(SqlConnection c, CancellationToken ct)
    {
        await using var command = new SqlCommand("SELECT Id, FullName FROM dbo.Employees WHERE IsActive = 1 ORDER BY Id;", c);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var list = new List<Employee>();
        while (await reader.ReadAsync(ct)) list.Add(new Employee(reader.GetInt32(0), reader.GetString(1)));
        return list;
    }

    private static async Task<List<ShiftRecord>> ReadShiftsAsync(SqlDataReader reader, CancellationToken ct)
    {
        var list = new List<ShiftRecord>();
        while (await reader.ReadAsync(ct))
            list.Add(new ShiftRecord(
                reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2),
                reader.GetDateTimeOffset(3), reader.IsDBNull(4) ? null : reader.GetDateTimeOffset(4),
                reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        return list;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
