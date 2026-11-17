namespace Attendance.Api.Domain;

public static class PunchActions
{
    public const string In = "in";
    public const string Out = "out";
}

public sealed record Employee(int Id, string Name);

/// <summary>A shift as stored in the database (UTC only).</summary>
public sealed record ShiftRecord(
    Guid Id,
    int EmployeeId,
    string EmployeeName,
    DateTimeOffset StartUtc,
    DateTimeOffset? EndUtc,
    string StartSource,
    string? EndSource);

/// <summary>A shift as returned to the client: UTC + Zurich local time with an explicit offset.</summary>
public sealed record ShiftDto(
    Guid Id,
    int EmployeeId,
    string EmployeeName,
    DateTimeOffset StartUtc,
    DateTimeOffset StartZurich,
    DateTimeOffset? EndUtc,
    DateTimeOffset? EndZurich,
    long? DurationSeconds,
    bool CrossesMidnight,
    bool SpansDstChange,
    string StartSource,
    string? EndSource)
{
    public static ShiftDto From(ShiftRecord s)
    {
        var startLocal = ZurichTime.ToZurich(s.StartUtc);
        DateTimeOffset? endLocal = s.EndUtc is { } end ? ZurichTime.ToZurich(end) : null;
        return new(
            s.Id, s.EmployeeId, s.EmployeeName,
            s.StartUtc, startLocal,
            s.EndUtc, endLocal,
            // Duration from UTC instants: correct on a 23- or 25-hour day too.
            s.EndUtc is { } e ? (long)(e - s.StartUtc).TotalSeconds : null,
            endLocal is { } el && DateOnly.FromDateTime(el.DateTime) != DateOnly.FromDateTime(startLocal.DateTime),
            endLocal is { } el2 && el2.Offset != startLocal.Offset,
            s.StartSource, s.EndSource);
    }
}

public sealed record EmployeeAttendance(
    Employee Employee,
    ShiftDto? OpenShift,
    IReadOnlyList<ShiftDto> Shifts,
    DateTimeOffset ServerNowUtc);

public sealed record PunchRequest(Guid RequestId, Guid? ShiftId);

public sealed record PunchReceipt(
    Guid RequestId,
    Guid ShiftId,
    string Action,
    DateTimeOffset AtUtc,
    DateTimeOffset AtZurich,
    string TimeSource,
    bool Replayed);

public sealed class AttendanceException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
