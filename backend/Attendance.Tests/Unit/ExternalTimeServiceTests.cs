using Attendance.Api.Domain;
using Attendance.Api.Time;
using Microsoft.Extensions.Logging.Abstractions;

namespace Attendance.Tests.Unit;

public class ExternalTimeServiceTests
{
    private static readonly DateTimeOffset Instant = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private static ExternalTimeService Service(params IZurichTimeSource[] sources) =>
        new(sources, new TimeApiOptions { AttemptTimeoutSeconds = 1, AttemptsPerSource = 2, RetryDelayMilliseconds = 1 },
            NullLogger<ExternalTimeService>.Instance);

    [Fact]
    public async Task Returns_primary_result_when_healthy()
    {
        var primary = new ScriptedSource("primary", _ => Task.FromResult(Instant));
        var secondary = new ScriptedSource("secondary", _ => Task.FromResult(Instant));

        var result = await Service(primary, secondary).GetCurrentAsync(CancellationToken.None);

        Assert.Equal("primary", result.Source);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(0, secondary.Calls);
    }

    [Fact]
    public async Task Retries_transient_failure_then_falls_back()
    {
        var primary = new ScriptedSource("primary", _ => throw new HttpRequestException("503"));
        var secondary = new ScriptedSource("secondary", _ => Task.FromResult(Instant));

        var result = await Service(primary, secondary).GetCurrentAsync(CancellationToken.None);

        Assert.Equal("secondary", result.Source);
        Assert.Equal(2, primary.Calls); // one retry on a transient error
    }

    [Fact]
    public async Task Invalid_data_is_not_retried()
    {
        var primary = new ScriptedSource("primary", _ => throw new InvalidDataException("bad zone"));
        var secondary = new ScriptedSource("secondary", _ => Task.FromResult(Instant));

        var result = await Service(primary, secondary).GetCurrentAsync(CancellationToken.None);

        Assert.Equal("secondary", result.Source);
        Assert.Equal(1, primary.Calls);
    }

    [Fact]
    public async Task Hanging_source_times_out_and_falls_back()
    {
        var primary = new ScriptedSource("primary", async ct => { await Task.Delay(Timeout.Infinite, ct); return Instant; });
        var secondary = new ScriptedSource("secondary", _ => Task.FromResult(Instant));

        var started = DateTime.UtcNow;
        var result = await Service(primary, secondary).GetCurrentAsync(CancellationToken.None);

        Assert.Equal("secondary", result.Source);
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(6));
    }

    [Fact]
    public async Task All_sources_failing_gives_503_time_unavailable()
    {
        var a = new ScriptedSource("a", _ => throw new HttpRequestException());
        var b = new ScriptedSource("b", _ => throw new InvalidDataException());

        var error = await Assert.ThrowsAsync<AttendanceException>(() => Service(a, b).GetCurrentAsync(CancellationToken.None));

        Assert.Equal(503, error.Status);
        Assert.Equal("time_unavailable", error.Code);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_provider_failure()
    {
        using var cts = new CancellationTokenSource();
        var source = new ScriptedSource("a", async ct => { await cts.CancelAsync(); ct.ThrowIfCancellationRequested(); return Instant; });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(source).GetCurrentAsync(cts.Token));
    }

    private sealed class ScriptedSource(string name, Func<CancellationToken, Task<DateTimeOffset>> next) : IZurichTimeSource
    {
        public int Calls;
        public string Name => name;

        public async Task<ExternalTime> GetAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return new ExternalTime(await next(cancellationToken), name);
        }
    }
}
