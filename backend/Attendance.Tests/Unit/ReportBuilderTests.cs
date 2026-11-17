using Attendance.Api.Domain;

namespace Attendance.Tests.Unit;

public class ReportBuilderTests
{
    private static readonly Employee Noa = new(1, "Noa");
    private static readonly Employee Yossi = new(2, "Yossi");

    private static DateTimeOffset Utc(int y, int m, int d, int h, int min = 0) => new(y, m, d, h, min, 0, TimeSpan.Zero);

    private static ShiftRecord Shift(Employee e, DateTimeOffset start, DateTimeOffset? end) =>
        new(Guid.NewGuid(), e.Id, e.Name, start, end, "test", end is null ? null : "test");

    [Fact]
    public void Night_shift_is_split_at_zurich_midnight()
    {
        // 22:00 → 06:00 Zurich time in winter (+01:00) = 21:00Z → 05:00Z
        var range = new ReportRange(new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 17));
        var report = ReportBuilder.Build(range, [Noa], [Shift(Noa, Utc(2026, 11, 16, 21), Utc(2026, 11, 17, 5))], Utc(2026, 11, 18, 0));

        var noa = report.Employees.Single();
        Assert.Equal(2 * 3600, noa.Days[0].Seconds);
        Assert.Equal(6 * 3600, noa.Days[1].Seconds);
        Assert.Equal(8 * 3600, noa.TotalSeconds);
        Assert.Equal(2, noa.DaysWorked);
        Assert.True(report.Shifts.Single().CrossesMidnight);
    }

    [Fact]
    public void Full_fall_back_day_counts_25_hours()
    {
        var day = new DateOnly(2026, 10, 25);
        var shift = Shift(Noa, ZurichTime.StartOfDayUtc(day), ZurichTime.StartOfDayUtc(day.AddDays(1)));
        var report = ReportBuilder.Build(new ReportRange(day, day), [Noa], [shift], Utc(2026, 11, 1, 0));

        Assert.Equal(25 * 3600, report.Employees.Single().TotalSeconds);
        Assert.Equal(25 * 3600, report.Shifts.Single().DurationSeconds);
        Assert.True(report.Shifts.Single().SpansDstChange);
    }

    [Fact]
    public void Shift_across_spring_forward_counts_real_elapsed_time()
    {
        // 01:00 → 04:00 on the clock = only 2 real hours (the hour 02:00-03:00 does not exist).
        var start = ZurichTime.FromLocal(new DateTime(2026, 3, 29, 1, 0, 0), null);
        var end = ZurichTime.FromLocal(new DateTime(2026, 3, 29, 4, 0, 0), null);
        var day = new DateOnly(2026, 3, 29);
        var report = ReportBuilder.Build(new ReportRange(day, day), [Noa], [Shift(Noa, start, end)], Utc(2026, 4, 1, 0));

        Assert.Equal(2 * 3600, report.Employees.Single().TotalSeconds);
        var dto = report.Shifts.Single();
        Assert.Equal(TimeSpan.FromHours(1), dto.StartZurich.Offset);
        Assert.Equal(TimeSpan.FromHours(2), dto.EndZurich!.Value.Offset);
    }

    [Fact]
    public void Open_shift_counts_until_now_and_is_flagged()
    {
        var day = new DateOnly(2026, 11, 17);
        var report = ReportBuilder.Build(new ReportRange(day, day), [Noa, Yossi],
            [Shift(Noa, Utc(2026, 11, 17, 7), null)], nowUtc: Utc(2026, 11, 17, 10));

        var noa = report.Employees.Single(e => e.EmployeeId == 1);
        Assert.True(noa.HasOpenShift);
        Assert.Equal(3 * 3600, noa.TotalSeconds);
        var yossi = report.Employees.Single(e => e.EmployeeId == 2);
        Assert.Equal(0, yossi.TotalSeconds);  // an employee without shifts still appears in the report
        Assert.Null(report.Shifts.Single().DurationSeconds);
    }

    [Fact]
    public void Open_shift_from_previous_day_is_clamped_to_report_range()
    {
        var day = new DateOnly(2026, 11, 17);
        var report = ReportBuilder.Build(new ReportRange(day, day), [Noa],
            [Shift(Noa, Utc(2026, 11, 16, 20), null)], nowUtc: Utc(2026, 11, 20, 0));

        Assert.Equal(24 * 3600, report.Employees.Single().TotalSeconds);
    }

    [Theory]
    [InlineData("2026-11-17", "2026-11-16", "invalid_range")]
    [InlineData("2026-13-01", null, "invalid_date")]
    [InlineData("17/11/2026", null, "invalid_date")]
    [InlineData("2026-01-01", "2026-12-31", "range_too_long")]
    public void Invalid_ranges_are_rejected(string from, string? to, string code)
    {
        var error = Assert.Throws<AttendanceException>(() => ReportRange.Parse(from, to, new DateOnly(2026, 11, 17)));
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void Missing_dates_default_to_today()
    {
        var range = ReportRange.Parse(null, null, new DateOnly(2026, 11, 17));
        Assert.Equal(new DateOnly(2026, 11, 17), range.From);
        Assert.Equal(new DateOnly(2026, 11, 17), range.To);
    }
}
