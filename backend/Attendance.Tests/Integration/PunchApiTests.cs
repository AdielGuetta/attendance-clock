using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Attendance.Tests.Integration;

/// <summary>
/// The time service is shared across the class's tests, so they run sequentially (xUnit's default within a class).
/// Each test creates its own employee.
/// </summary>
public class PunchApiTests(AttendanceApiFactory app) : IClassFixture<AttendanceApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http = app.CreateClient();

    private sealed record Receipt(Guid RequestId, Guid ShiftId, string Action, DateTimeOffset AtUtc, DateTimeOffset AtZurich, string TimeSource, bool Replayed);
    private sealed record ShiftView(Guid Id, DateTimeOffset StartUtc, DateTimeOffset StartZurich, DateTimeOffset? EndUtc, long? DurationSeconds, bool SpansDstChange);
    private sealed record Attendance(ShiftView? OpenShift, List<ShiftView> Shifts);
    private sealed record Day(DateOnly Date, long Seconds);
    private sealed record Summary(int EmployeeId, long TotalSeconds, bool HasOpenShift, List<Day> Days);
    private sealed record Report(List<Summary> Employees, List<ShiftView> Shifts);

    private Task<HttpResponseMessage> ClockIn(int employee, Guid? requestId = null) =>
        _http.PostAsJsonAsync($"/api/employees/{employee}/clock-in", new { requestId = requestId ?? Guid.NewGuid() });

    private Task<HttpResponseMessage> ClockOut(int employee, Guid shiftId, Guid? requestId = null) =>
        _http.PostAsJsonAsync($"/api/employees/{employee}/clock-out", new { requestId = requestId ?? Guid.NewGuid(), shiftId });

    private static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, Json)!;
    }

    private static async Task<string> ErrorCode(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("code").GetString()!;
    }

    private Task<Attendance> GetAttendance(int employee) =>
        _http.GetFromJsonAsync<Attendance>($"/api/employees/{employee}/attendance", Json)!;

    [SqlFact]
    public async Task Clock_in_then_out_uses_external_time_and_stores_source()
    {
        var employee = await app.CreateEmployeeAsync();
        app.Time.Set(new DateTimeOffset(2026, 11, 17, 7, 0, 0, TimeSpan.Zero));
        var started = await Read<Receipt>(await ClockIn(employee));

        Assert.Equal(new DateTimeOffset(2026, 11, 17, 7, 0, 0, TimeSpan.Zero), started.AtUtc);
        Assert.Equal(TimeSpan.FromHours(1), started.AtZurich.Offset);
        Assert.Equal("fake", started.TimeSource);
        Assert.False(started.Replayed);

        var state = await GetAttendance(employee);
        Assert.Equal(started.ShiftId, state.OpenShift?.Id);

        app.Time.Set(new DateTimeOffset(2026, 11, 17, 15, 30, 0, TimeSpan.Zero));
        var ended = await Read<Receipt>(await ClockOut(employee, started.ShiftId));
        Assert.Equal("out", ended.Action);

        state = await GetAttendance(employee);
        Assert.Null(state.OpenShift);
        Assert.Equal((long)(8.5 * 3600), state.Shifts.Single().DurationSeconds);
    }

    [SqlFact]
    public async Task Time_service_failure_rejects_punch_without_writing()
    {
        var employee = await app.CreateEmployeeAsync();
        app.Time.Fail = true;
        try
        {
            Assert.Equal("time_unavailable", await ErrorCode(await ClockIn(employee), HttpStatusCode.ServiceUnavailable));
        }
        finally { app.Time.Fail = false; }

        Assert.Equal(0, await app.CountAsync("Shifts", employee));
        Assert.Equal(0, await app.CountAsync("Punches", employee));
    }

    [SqlFact]
    public async Task Double_click_with_same_request_id_creates_one_shift()
    {
        var employee = await app.CreateEmployeeAsync();
        var requestId = Guid.NewGuid();
        app.Time.Delay = TimeSpan.FromMilliseconds(300); // both requests are in flight at the same time
        try
        {
            var responses = await Task.WhenAll(ClockIn(employee, requestId), ClockIn(employee, requestId));
            var receipts = await Task.WhenAll(responses.Select(r => Read<Receipt>(r)));
            Assert.Equal(receipts[0].ShiftId, receipts[1].ShiftId);
            Assert.Equal(receipts[0].AtUtc, receipts[1].AtUtc);
            Assert.Single(receipts, r => r.Replayed);
        }
        finally { app.Time.Delay = TimeSpan.Zero; }

        Assert.Equal(1, await app.CountAsync("Shifts", employee));
        Assert.Equal(1, await app.CountAsync("Punches", employee));
    }

    [SqlFact]
    public async Task Concurrent_clock_ins_with_different_ids_allow_only_one()
    {
        var employee = await app.CreateEmployeeAsync();
        app.Time.Delay = TimeSpan.FromMilliseconds(300);
        HttpResponseMessage[] responses;
        try
        {
            responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => ClockIn(employee)));
        }
        finally { app.Time.Delay = TimeSpan.Zero; }

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK),
            r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(1, await app.CountAsync("Shifts", employee));
    }

    [SqlFact]
    public async Task Retry_after_lost_response_returns_original_receipt_without_calling_time_service()
    {
        var employee = await app.CreateEmployeeAsync();
        var requestId = Guid.NewGuid();
        app.Time.Set(new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero));
        var first = await Read<Receipt>(await ClockIn(employee, requestId));

        var callsBefore = app.Time.Calls;
        app.Time.Fail = true; // even if the time service is down, a retry returns the saved receipt
        try
        {
            var retry = await Read<Receipt>(await ClockIn(employee, requestId));
            Assert.True(retry.Replayed);
            Assert.Equal(first.AtUtc, retry.AtUtc);
            Assert.Equal(first.ShiftId, retry.ShiftId);
        }
        finally { app.Time.Fail = false; }
        Assert.Equal(callsBefore, app.Time.Calls);

        // After a refresh: the client can check whether a punch was saved
        var found = await Read<Receipt>(await _http.GetAsync($"/api/employees/{employee}/punches/{requestId}"));
        Assert.Equal(first.ShiftId, found.ShiftId);
        Assert.Equal("punch_not_found",
            await ErrorCode(await _http.GetAsync($"/api/employees/{employee}/punches/{Guid.NewGuid()}"), HttpStatusCode.NotFound));
    }

    [SqlFact]
    public async Task Reusing_request_id_for_other_action_is_rejected()
    {
        var employee = await app.CreateEmployeeAsync();
        var requestId = Guid.NewGuid();
        var started = await Read<Receipt>(await ClockIn(employee, requestId));
        Assert.Equal("request_reused", await ErrorCode(await ClockOut(employee, started.ShiftId, requestId), HttpStatusCode.Conflict));
    }

    [SqlFact]
    public async Task Stale_screen_cannot_close_a_different_shift()
    {
        var employee = await app.CreateEmployeeAsync();
        app.Time.Set(new DateTimeOffset(2026, 11, 17, 7, 0, 0, TimeSpan.Zero));
        var first = await Read<Receipt>(await ClockIn(employee));
        app.Time.Set(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero));
        await Read<Receipt>(await ClockOut(employee, first.ShiftId));
        app.Time.Set(new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero));
        await Read<Receipt>(await ClockIn(employee)); // a new shift opened in another tab

        // An old tab still shows the first shift and tries to close it
        app.Time.Set(new DateTimeOffset(2026, 11, 17, 11, 0, 0, TimeSpan.Zero));
        Assert.Equal("shift_changed", await ErrorCode(await ClockOut(employee, first.ShiftId), HttpStatusCode.Conflict));
    }

    [SqlFact]
    public async Task Invalid_state_transitions_return_409_before_calling_time_service()
    {
        var employee = await app.CreateEmployeeAsync();
        var calls = app.Time.Calls;
        Assert.Equal("no_open_shift", await ErrorCode(await ClockOut(employee, Guid.NewGuid()), HttpStatusCode.Conflict));
        Assert.Equal(calls, app.Time.Calls);

        await Read<Receipt>(await ClockIn(employee));
        calls = app.Time.Calls;
        Assert.Equal("shift_already_open", await ErrorCode(await ClockIn(employee), HttpStatusCode.Conflict));
        Assert.Equal(calls, app.Time.Calls);
    }

    [SqlFact]
    public async Task Clock_out_earlier_than_clock_in_is_rejected()
    {
        var employee = await app.CreateEmployeeAsync();
        app.Time.Set(new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero));
        var started = await Read<Receipt>(await ClockIn(employee));
        app.Time.Set(new DateTimeOffset(2026, 11, 17, 9, 59, 0, TimeSpan.Zero));
        Assert.Equal("time_before_start", await ErrorCode(await ClockOut(employee, started.ShiftId), HttpStatusCode.Conflict));
        Assert.NotNull((await GetAttendance(employee)).OpenShift);
    }

    [SqlFact]
    public async Task Bad_input_is_rejected()
    {
        var employee = await app.CreateEmployeeAsync();
        Assert.Equal("invalid_punch", await ErrorCode(await ClockIn(employee, Guid.Empty), HttpStatusCode.BadRequest));
        Assert.Equal("employee_not_found", await ErrorCode(await ClockIn(99999), HttpStatusCode.NotFound));
        var malformed = await _http.PostAsync($"/api/employees/{employee}/clock-in", new StringContent("{", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [SqlFact]
    public async Task Night_shift_across_fall_back_is_reported_per_zurich_day()
    {
        var employee = await app.CreateEmployeeAsync();
        // Clock-in at 22:00 Zurich time on Saturday 2026-10-24 (summer time, +02:00) = 20:00Z
        app.Time.Set(new DateTimeOffset(2026, 10, 24, 20, 0, 0, TimeSpan.Zero));
        var started = await Read<Receipt>(await ClockIn(employee));
        Assert.Equal(TimeSpan.FromHours(2), started.AtZurich.Offset);
        // Clock-out at 06:00 Zurich time on Sunday 2026-10-25 (winter time, +01:00) = 05:00Z → 9 real hours
        app.Time.Set(new DateTimeOffset(2026, 10, 25, 5, 0, 0, TimeSpan.Zero));
        var ended = await Read<Receipt>(await ClockOut(employee, started.ShiftId));
        Assert.Equal(TimeSpan.FromHours(1), ended.AtZurich.Offset);

        var report = await _http.GetFromJsonAsync<Report>("/api/report?from=2026-10-24&to=2026-10-25", Json);
        var mine = report!.Employees.Single(e => e.EmployeeId == employee);
        Assert.Equal(9 * 3600, mine.TotalSeconds);
        Assert.Equal(2 * 3600, mine.Days.Single(d => d.Date == new DateOnly(2026, 10, 24)).Seconds);
        Assert.Equal(7 * 3600, mine.Days.Single(d => d.Date == new DateOnly(2026, 10, 25)).Seconds);
        var shift = report.Shifts.Single(s => s.Id == started.ShiftId);
        Assert.Equal(9 * 3600, shift.DurationSeconds);
        Assert.True(shift.SpansDstChange);

        Assert.Equal("invalid_date", await ErrorCode(await _http.GetAsync("/api/report?from=2026-10-99"), HttpStatusCode.BadRequest));
    }

    [SqlFact]
    public async Task Open_shift_survives_refresh_and_shows_in_report()
    {
        var employee = await app.CreateEmployeeAsync();
        app.Time.Set(new DateTimeOffset(2026, 11, 17, 7, 0, 0, TimeSpan.Zero));
        var started = await Read<Receipt>(await ClockIn(employee));
        // AdjustTime and not SetUtcNow: the fake server clock starts at 12:00, and SetUtcNow refuses to go back in time.
        app.Clock.AdjustTime(new DateTimeOffset(2026, 11, 17, 9, 15, 0, TimeSpan.Zero));

        // A "refresh" = a new request. The open shift comes back from the server with the original clock-in time.
        var state = await GetAttendance(employee);
        Assert.Equal(started.ShiftId, state.OpenShift!.Id);
        Assert.Equal(started.AtUtc, state.OpenShift.StartUtc);

        var report = await _http.GetFromJsonAsync<Report>("/api/report?from=2026-11-17", Json);
        var mine = report!.Employees.Single(e => e.EmployeeId == employee);
        Assert.True(mine.HasOpenShift);
        Assert.Equal((long)TimeSpan.FromHours(2.25).TotalSeconds, mine.TotalSeconds);
    }
}
