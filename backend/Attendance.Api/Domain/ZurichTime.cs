namespace Attendance.Api.Domain;

/// <summary>
/// All conversions between UTC and Zurich local time go through here.
/// Rule: store UTC, compute durations from UTC instants, and convert only for display or day boundaries.
/// </summary>
public static class ZurichTime
{
    public const string ZoneId = "Europe/Zurich";

    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById(ZoneId);

    /// <summary>The same instant with the Zurich offset in effect at that moment (+01:00 or +02:00).</summary>
    public static DateTimeOffset ToZurich(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone);

    public static DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(ToZurich(instant).DateTime);

    /// <summary>
    /// The UTC instant of Zurich midnight. In Zurich the clock changes at 02:00/03:00, so midnight always
    /// exists exactly once. That is why the day boundary is well defined even on a 23- or 25-hour day.
    /// </summary>
    public static DateTimeOffset StartOfDayUtc(DateOnly date) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), Zone), TimeSpan.Zero);

    /// <summary>
    /// Converts Zurich local time (no offset) to UTC.
    /// A time inside the spring-forward gap (02:00-03:00) is invalid and is rejected.
    /// In the repeated autumn hour (02:00-03:00 occurs twice) the <paramref name="isDaylightSaving"/> flag is required to decide.
    /// </summary>
    public static DateTimeOffset FromLocal(DateTime local, bool? isDaylightSaving)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (Zone.IsInvalidTime(local))
            throw new InvalidDataException($"Local time {local:O} does not exist in {ZoneId} (spring-forward gap).");

        TimeSpan offset;
        if (Zone.IsAmbiguousTime(local))
        {
            if (isDaylightSaving is null)
                throw new InvalidDataException($"Local time {local:O} is ambiguous in {ZoneId} and no DST flag was provided.");
            var offsets = Zone.GetAmbiguousTimeOffsets(local);
            offset = isDaylightSaving.Value ? offsets.Max() : offsets.Min();
        }
        else
        {
            offset = Zone.GetUtcOffset(local);
            if (isDaylightSaving is bool flag && Zone.IsDaylightSavingTime(local) != flag)
                throw new InvalidDataException($"DST flag {flag} contradicts {ZoneId} rules for {local:O}.");
        }

        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
