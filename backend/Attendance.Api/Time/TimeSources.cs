using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Attendance.Api.Domain;

namespace Attendance.Api.Time;

/// <summary>A time read from an external source. <see cref="Source"/> is stored next to every punch for auditing.</summary>
public sealed record ExternalTime(DateTimeOffset UtcNow, string Source);

/// <summary>One external time provider. Throws <see cref="InvalidDataException"/> on an invalid response.</summary>
public interface IZurichTimeSource
{
    string Name { get; }
    Task<ExternalTime> GetAsync(CancellationToken cancellationToken);
}

public abstract class HttpTimeSource(HttpClient http, Uri endpoint) : IZurichTimeSource
{
    public abstract string Name { get; }

    public async Task<ExternalTime> GetAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        var utc = Parse(json.RootElement);
        if (utc.Year is < 2000 or > 2100)
            throw new InvalidDataException($"{Name} returned an implausible year {utc.Year}.");
        return new ExternalTime(utc, Name);
    }

    protected abstract DateTimeOffset Parse(JsonElement root);

    protected static string RequireString(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : throw new InvalidDataException($"Missing string property '{property}'.");
}

/// <summary>
/// A WorldTimeAPI-compatible provider (default: worldtime.timezone.io).
/// Uses the explicit <c>utc_datetime</c> field and requires a zero offset. Also checks that <c>datetime</c> is the same instant.
/// </summary>
public sealed class WorldTimeApiSource(HttpClient http, Uri endpoint) : HttpTimeSource(http, endpoint)
{
    private static readonly string[] Formats = ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK"];

    public override string Name => "worldtimeapi";

    protected override DateTimeOffset Parse(JsonElement root)
    {
        if (RequireString(root, "timezone") != ZurichTime.ZoneId)
            throw new InvalidDataException("Response is not for Europe/Zurich.");

        var utc = ParseInstant(RequireString(root, "utc_datetime"));
        if (utc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("utc_datetime must have a zero offset.");

        if (root.TryGetProperty("datetime", out var local) && local.ValueKind == JsonValueKind.String)
        {
            var localInstant = ParseInstant(local.GetString()!);
            if (Math.Abs((localInstant - utc).TotalSeconds) > 1)
                throw new InvalidDataException("datetime and utc_datetime describe different instants.");
        }
        return utc.ToUniversalTime();
    }

    internal static DateTimeOffset ParseInstant(string value) =>
        DateTimeOffset.TryParseExact(value, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)
            ? instant
            : throw new InvalidDataException($"Cannot parse timestamp '{value}'.");
}

/// <summary>
/// timeapi.io provider. Returns Zurich local time <b>without an offset</b> plus a <c>dstActive</c> flag.
/// The conversion to UTC goes through <see cref="ZurichTime.FromLocal"/>: the flag resolves the repeated autumn hour,
/// and a time inside the spring-forward gap is rejected.
/// </summary>
public sealed class TimeApiIoSource(HttpClient http, Uri endpoint) : HttpTimeSource(http, endpoint)
{
    private static readonly string[] Formats = ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss"];

    public override string Name => "timeapi.io";

    protected override DateTimeOffset Parse(JsonElement root)
    {
        if (RequireString(root, "timeZone") != ZurichTime.ZoneId)
            throw new InvalidDataException("Response is not for Europe/Zurich.");

        var text = RequireString(root, "dateTime");
        if (!DateTime.TryParseExact(text, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            throw new InvalidDataException($"Cannot parse local time '{text}'.");

        bool? dst = root.TryGetProperty("dstActive", out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? flag.GetBoolean()
            : null;
        return ZurichTime.FromLocal(local, dst);
    }
}

public sealed class TimeApiOptions
{
    public const string Section = "TimeApi";

    /// <summary>Time limit for each single call to a provider.</summary>
    public int AttemptTimeoutSeconds { get; set; } = 4;

    /// <summary>Number of attempts per provider on a transient error (network, timeout, 5xx). Invalid data is not retried.</summary>
    public int AttemptsPerSource { get; set; } = 2;

    public int RetryDelayMilliseconds { get; set; } = 250;

    public List<TimeSourceOptions> Sources { get; set; } = [];
}

public sealed class TimeSourceOptions
{
    /// <summary>"WorldTimeApi" or "TimeApiIo".</summary>
    public string Kind { get; set; } = "";
    public string Url { get; set; } = "";
}
