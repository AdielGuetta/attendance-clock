using System.Globalization;

namespace Attendance.Api.Domain;

public sealed record ReportRange(DateOnly From, DateOnly To)
{
    public const int MaxDays = 92;

    public DateTimeOffset StartUtc => ZurichTime.StartOfDayUtc(From);
    public DateTimeOffset EndUtcExclusive => ZurichTime.StartOfDayUtc(To.AddDays(1));

    public IEnumerable<DateOnly> Days()
    {
        for (var d = From; d <= To; d = d.AddDays(1)) yield return d;
    }

    public static ReportRange Parse(string? from, string? to, DateOnly today)
    {
        var start = from is null ? today : ParseDate(from);
        var end = to is null ? start : ParseDate(to);
        if (end < start)
            throw new AttendanceException(400, "invalid_range", "תאריך הסיום קודם לתאריך ההתחלה.");
        if (end.DayNumber - start.DayNumber + 1 > MaxDays)
            throw new AttendanceException(400, "range_too_long", $"אפשר להציג עד {MaxDays} ימים בדוח אחד.");
        return new(start, end);
    }

    private static DateOnly ParseDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
        && d.Year is >= 2000 and <= 2100
            ? d
            : throw new AttendanceException(400, "invalid_date", "יש לבחור תאריך תקין בפורמט YYYY-MM-DD.");
}

public sealed record DayTotal(DateOnly Date, long Seconds);

public sealed record EmployeeSummary(
    int EmployeeId,
    string EmployeeName,
    long TotalSeconds,
    int DaysWorked,
    int ShiftCount,
    bool HasOpenShift,
    IReadOnlyList<DayTotal> Days);

public sealed record AttendanceReport(
    DateOnly From,
    DateOnly To,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<EmployeeSummary> Employees,
    IReadOnlyList<ShiftDto> Shifts);

public static class ReportBuilder
{
    /// <summary>
    /// Splits each shift between the Zurich days it spans, using real UTC instants.
    /// A shift that is still open is counted up to <paramref name="nowUtc"/> (clamped to the end of the range) and flagged as open.
    /// </summary>
    public static AttendanceReport Build(
        ReportRange range,
        IReadOnlyList<Employee> employees,
        IReadOnlyList<ShiftRecord> shifts,
        DateTimeOffset nowUtc)
    {
        var days = range.Days()
            .Select(d => (Date: d, Start: ZurichTime.StartOfDayUtc(d), End: ZurichTime.StartOfDayUtc(d.AddDays(1))))
            .ToList();

        var summaries = employees.Select(employee =>
        {
            var own = shifts.Where(s => s.EmployeeId == employee.Id).ToList();
            var perDay = days.Select(day => new DayTotal(day.Date, own.Sum(s =>
            {
                var end = s.EndUtc ?? (nowUtc > s.StartUtc ? nowUtc : s.StartUtc);
                var from = s.StartUtc > day.Start ? s.StartUtc : day.Start;
                var to = end < day.End ? end : day.End;
                return to > from ? (long)(to - from).TotalSeconds : 0L;
            }))).ToList();

            return new EmployeeSummary(
                employee.Id,
                employee.Name,
                perDay.Sum(d => d.Seconds),
                perDay.Count(d => d.Seconds > 0),
                own.Count,
                own.Any(s => s.EndUtc is null),
                perDay);
        }).ToList();

        var shiftDtos = shifts
            .OrderBy(s => s.StartUtc).ThenBy(s => s.EmployeeName, StringComparer.Ordinal)
            .Select(ShiftDto.From)
            .ToList();

        return new AttendanceReport(range.From, range.To, nowUtc, summaries, shiftDtos);
    }
}
