using Attendance.Api.Domain;

namespace Attendance.Tests.Unit;

/// <summary>
/// DST transitions in Zurich in 2026:
/// spring forward: Sunday 2026-03-29, 02:00 → 03:00 (a 23-hour day)
/// fall back:      Sunday 2026-10-25, 03:00 → 02:00 (a 25-hour day)
/// </summary>
public class ZurichTimeTests
{
    [Fact]
    public void Spring_forward_day_has_23_hours()
    {
        var start = ZurichTime.StartOfDayUtc(new DateOnly(2026, 3, 29));
        var end = ZurichTime.StartOfDayUtc(new DateOnly(2026, 3, 30));
        Assert.Equal(new DateTimeOffset(2026, 3, 28, 23, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(TimeSpan.FromHours(23), end - start);
    }

    [Fact]
    public void Fall_back_day_has_25_hours()
    {
        var start = ZurichTime.StartOfDayUtc(new DateOnly(2026, 10, 25));
        var end = ZurichTime.StartOfDayUtc(new DateOnly(2026, 10, 26));
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 22, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(TimeSpan.FromHours(25), end - start);
    }

    [Fact]
    public void Local_time_in_spring_gap_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => ZurichTime.FromLocal(new DateTime(2026, 3, 29, 2, 30, 0), false));
    }

    [Theory]
    [InlineData(true, 0)]   // first 02:30 (summer time, +02:00) = 00:30Z
    [InlineData(false, 1)]  // second 02:30 (winter time, +01:00) = 01:30Z
    public void Ambiguous_autumn_hour_is_resolved_by_dst_flag(bool dst, int expectedUtcHour)
    {
        var utc = ZurichTime.FromLocal(new DateTime(2026, 10, 25, 2, 30, 0), dst);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, expectedUtcHour, 30, 0, TimeSpan.Zero), utc);
    }

    [Fact]
    public void Ambiguous_autumn_hour_without_flag_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => ZurichTime.FromLocal(new DateTime(2026, 10, 25, 2, 30, 0), null));
    }

    [Fact]
    public void Dst_flag_that_contradicts_zone_rules_is_rejected()
    {
        // In November there is no summer time, so a dstActive=true flag points to a broken response.
        Assert.Throws<InvalidDataException>(() => ZurichTime.FromLocal(new DateTime(2026, 11, 17, 12, 0, 0), true));
    }

    [Fact]
    public void ToZurich_uses_offset_in_effect_at_that_instant()
    {
        Assert.Equal(TimeSpan.FromHours(2), ZurichTime.ToZurich(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero)).Offset);
        Assert.Equal(TimeSpan.FromHours(1), ZurichTime.ToZurich(new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero)).Offset);
    }
}
