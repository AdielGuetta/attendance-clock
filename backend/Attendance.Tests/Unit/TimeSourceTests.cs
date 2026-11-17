using System.Net;
using System.Text.Json;
using Attendance.Api.Time;

namespace Attendance.Tests.Unit;

public class TimeSourceTests
{
    private static readonly Uri Endpoint = new("https://time.example/zurich");

    private static HttpClient Respond(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) })));

    // A sample response captured from worldtime.timezone.io on 2026-11-17.
    private const string WorldTimeSample = """
        {"utc_offset":"+01:00","timezone":"Europe/Zurich","datetime":"2026-11-17T21:59:04.366436+01:00",
         "utc_datetime":"2026-11-17T20:59:04.366436+00:00","dst":false,"abbreviation":"CET"}
        """;

    [Fact]
    public async Task WorldTimeApi_parses_utc_datetime()
    {
        var result = await new WorldTimeApiSource(Respond(WorldTimeSample), Endpoint).GetAsync(CancellationToken.None);
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 20, 59, 4, TimeSpan.Zero).AddTicks(3664360), result.UtcNow);
        Assert.Equal("worldtimeapi", result.Source);
    }

    [Theory]
    [InlineData("""{"timezone":"Europe/Berlin","utc_datetime":"2026-11-17T20:59:04Z"}""")]
    [InlineData("""{"timezone":"Europe/Zurich","utc_datetime":"2026-11-17T21:59:04+01:00"}""")]
    [InlineData("""{"timezone":"Europe/Zurich"}""")]
    [InlineData("""{"timezone":"Europe/Zurich","utc_datetime":"yesterday"}""")]
    [InlineData("""{"timezone":"Europe/Zurich","utc_datetime":"2026-11-17T20:59:04Z","datetime":"2026-11-17T23:59:04+01:00"}""")]
    [InlineData("""{"timezone":"Europe/Zurich","utc_datetime":"1970-01-01T00:00:00Z"}""")]
    [InlineData("""[]""")]
    public async Task WorldTimeApi_rejects_invalid_payloads(string body)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new WorldTimeApiSource(Respond(body), Endpoint).GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WorldTimeApi_rejects_malformed_json()
    {
        await Assert.ThrowsAnyAsync<JsonException>(() =>
            new WorldTimeApiSource(Respond("{"), Endpoint).GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Http_error_status_throws_HttpRequestException()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new WorldTimeApiSource(Respond("", HttpStatusCode.ServiceUnavailable), Endpoint).GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TimeApiIo_converts_local_time_to_utc()
    {
        const string body = """{"dateTime":"2026-11-17T21:59:05.0588747","timeZone":"Europe/Zurich","dstActive":false}""";
        var result = await new TimeApiIoSource(Respond(body), Endpoint).GetAsync(CancellationToken.None);
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 20, 59, 5, TimeSpan.Zero).AddTicks(588747), result.UtcNow);
        Assert.Equal("timeapi.io", result.Source);
    }

    [Theory]
    [InlineData(true, "2026-10-25T00:30:00Z")]
    [InlineData(false, "2026-10-25T01:30:00Z")]
    public async Task TimeApiIo_uses_dst_flag_in_repeated_autumn_hour(bool dst, string expected)
    {
        var body = $$"""{"dateTime":"2026-10-25T02:30:00","timeZone":"Europe/Zurich","dstActive":{{(dst ? "true" : "false")}}}""";
        var result = await new TimeApiIoSource(Respond(body), Endpoint).GetAsync(CancellationToken.None);
        Assert.Equal(DateTimeOffset.Parse(expected), result.UtcNow);
    }

    [Theory]
    [InlineData("""{"dateTime":"2026-03-29T02:30:00","timeZone":"Europe/Zurich","dstActive":false}""")] // spring gap
    [InlineData("""{"dateTime":"2026-10-25T02:30:00","timeZone":"Europe/Zurich"}""")]                     // ambiguous, no flag
    [InlineData("""{"dateTime":"2026-11-17T21:59:05","timeZone":"UTC","dstActive":false}""")]             // wrong zone
    public async Task TimeApiIo_rejects_unresolvable_times(string body)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new TimeApiIoSource(Respond(body), Endpoint).GetAsync(CancellationToken.None));
    }
}

internal sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        respond(request, cancellationToken);
}
