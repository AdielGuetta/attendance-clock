using System.Text.Json;
using Attendance.Api.Domain;

namespace Attendance.Api.Time;

/// <summary>The only source of time for a punch. There is no fallback to the server clock.</summary>
public interface IExternalTimeService
{
    Task<ExternalTime> GetCurrentAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Tries the providers in order. A transient error (network, timeout, 5xx) gets another attempt with the same provider.
/// Invalid data moves straight to the next provider. If every provider fails → 503 and the punch is not saved.
/// </summary>
public sealed class ExternalTimeService(
    IReadOnlyList<IZurichTimeSource> sources,
    TimeApiOptions options,
    ILogger<ExternalTimeService> logger) : IExternalTimeService
{
    public async Task<ExternalTime> GetCurrentAsync(CancellationToken cancellationToken)
    {
        foreach (var source in sources)
        {
            for (var attempt = 1; attempt <= options.AttemptsPerSource; attempt++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.AttemptTimeoutSeconds));
                try
                {
                    var result = await source.GetAsync(timeout.Token);
                    if (attempt > 1 || source != sources[0])
                        logger.LogInformation("Time obtained from {Source} on attempt {Attempt}.", source.Name, attempt);
                    return result;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsProviderFailure(ex))
                {
                    var transient = ex is HttpRequestException or OperationCanceledException or IOException;
                    logger.LogWarning("Time source {Source} failed on attempt {Attempt}: {ErrorType} {Message}",
                        source.Name, attempt, ex.GetType().Name, ex.Message);
                    if (!transient) break;
                    if (attempt < options.AttemptsPerSource)
                        await Task.Delay(options.RetryDelayMilliseconds, cancellationToken);
                }
            }
        }

        throw new AttendanceException(503, "time_unavailable",
            "שירות השעה החיצוני אינו זמין כרגע. ההחתמה לא נשמרה. אפשר לנסות שוב בעוד רגע.");
    }

    private static bool IsProviderFailure(Exception ex) =>
        ex is HttpRequestException or OperationCanceledException or IOException
            or JsonException or InvalidDataException or NotSupportedException;
}
