using System.Net;
using Attendance.Api.Data;
using Attendance.Api.Domain;
using Attendance.Api.Time;
using Microsoft.Data.SqlClient;

var initOnly = args.Contains("--init-db");
var builder = WebApplication.CreateBuilder(args.Where(a => a != "--init-db").ToArray());

// The connection string is read from IConfiguration at use time (not here), so settings that a test host
// (WebApplicationFactory) adds just before Build are honored too.
static string ConnectionString(IConfiguration configuration) =>
    configuration.GetConnectionString("Attendance") is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException("Set ConnectionStrings:Attendance (or the env var ConnectionStrings__Attendance).");

if (initOnly)
{
    await DatabaseSetup.InitializeAsync(ConnectionString(builder.Configuration));
    Console.WriteLine("Attendance database is ready.");
    return;
}

// ---------- External time ----------
var timeOptions = builder.Configuration.GetSection(TimeApiOptions.Section).Get<TimeApiOptions>() ?? new TimeApiOptions();
if (timeOptions.Sources.Count == 0)
    throw new InvalidOperationException("Configure at least one TimeApi:Sources entry.");
if (timeOptions.AttemptTimeoutSeconds is < 1 or > 30 || timeOptions.AttemptsPerSource is < 1 or > 5)
    throw new InvalidOperationException("TimeApi:AttemptTimeoutSeconds must be 1-30 and AttemptsPerSource 1-5.");

var sourceEndpoints = timeOptions.Sources.Select(s =>
{
    if (!Uri.TryCreate(s.Url, UriKind.Absolute, out var uri))
        throw new InvalidOperationException($"Invalid TimeApi source URL '{s.Url}'.");
    // HTTPS only. Plain HTTP is allowed only for loopback in Development (a fake server in tests).
    if (uri.Scheme != Uri.UriSchemeHttps && !(uri.IsLoopback && builder.Environment.IsDevelopment()))
        throw new InvalidOperationException($"TimeApi source '{s.Url}' must use HTTPS.");
    return (s.Kind, Uri: uri);
}).ToList();

builder.Services.AddSingleton(timeOptions);
builder.Services.AddHttpClient("time", client => client.Timeout = Timeout.InfiniteTimeSpan) // the timeout is managed per attempt in ExternalTimeService
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
builder.Services.AddSingleton<IReadOnlyList<IZurichTimeSource>>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return sourceEndpoints.Select<(string Kind, Uri Uri), IZurichTimeSource>(s => s.Kind.ToLowerInvariant() switch
    {
        "worldtimeapi" => new WorldTimeApiSource(factory.CreateClient("time"), s.Uri),
        "timeapiio" => new TimeApiIoSource(factory.CreateClient("time"), s.Uri),
        _ => throw new InvalidOperationException($"Unknown TimeApi source kind '{s.Kind}'.")
    }).ToList();
});
builder.Services.AddSingleton<IExternalTimeService, ExternalTimeService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped(sp => new AttendanceRepository(
    ConnectionString(sp.GetRequiredService<IConfiguration>()),
    sp.GetRequiredService<IExternalTimeService>(),
    sp.GetRequiredService<TimeProvider>()));

var app = builder.Build();

if (app.Configuration.GetValue("Database:InitializeOnStartup", false))
    await DatabaseSetup.InitializeAsync(ConnectionString(app.Configuration));

// ---------- Middleware: loopback only, no caching, errors as JSON ----------
var allowRemote = app.Configuration.GetValue("AllowRemoteClients", false);
app.Use(async (context, next) =>
{
    var remote = context.Connection.RemoteIpAddress;
    // TestServer has no IP address, so null is allowed.
    if (!allowRemote && remote is not null && !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    if (context.Request.Path.StartsWithSegments("/api"))
        context.Response.Headers.CacheControl = "no-store";

    try
    {
        await next(context);
    }
    catch (AttendanceException ex) when (!context.Response.HasStarted)
    {
        await Results.Json(new { code = ex.Code, error = ex.Message }, statusCode: ex.Status).ExecuteAsync(context);
    }
    catch (BadHttpRequestException ex) when (!context.Response.HasStarted)
    {
        app.Logger.LogInformation("Bad request: {Message}", ex.Message);
        await Results.Json(new { code = "bad_request", error = "הבקשה אינה תקינה." }, statusCode: 400).ExecuteAsync(context);
    }
    catch (SqlException ex) when (!context.Response.HasStarted)
    {
        app.Logger.LogError(ex, "SQL Server error {Number}.", ex.Number);
        await Results.Json(new { code = "database_unavailable", error = "מסד הנתונים אינו זמין כרגע. נסו שוב בעוד רגע." },
            statusCode: 503).ExecuteAsync(context);
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

// ---------- API ----------
var api = app.MapGroup("/api");

api.MapGet("/health", () => Results.Ok(new { status = "ok" }));

api.MapGet("/employees", (AttendanceRepository repo, CancellationToken ct) => repo.GetEmployeesAsync(ct));

api.MapGet("/employees/{employeeId:int}/attendance", (int employeeId, AttendanceRepository repo, CancellationToken ct) =>
    repo.GetAttendanceAsync(employeeId, ct));

api.MapGet("/employees/{employeeId:int}/punches/{requestId:guid}", async (int employeeId, Guid requestId, AttendanceRepository repo, CancellationToken ct) =>
    await repo.FindPunchAsync(employeeId, requestId, ct) is { } receipt
        ? Results.Ok(receipt)
        : Results.Json(new { code = "punch_not_found", error = "ההחתמה לא נשמרה." }, statusCode: 404));

api.MapPost("/employees/{employeeId:int}/clock-in", (int employeeId, PunchRequest request, AttendanceRepository repo, CancellationToken ct) =>
    repo.PunchAsync(employeeId, PunchActions.In, request, ct));

api.MapPost("/employees/{employeeId:int}/clock-out", (int employeeId, PunchRequest request, AttendanceRepository repo, CancellationToken ct) =>
    repo.PunchAsync(employeeId, PunchActions.Out, request, ct));

api.MapGet("/report", (string? from, string? to, AttendanceRepository repo, TimeProvider clock, CancellationToken ct) =>
    repo.GetReportAsync(ReportRange.Parse(from, to, ZurichTime.LocalDate(clock.GetUtcNow())), ct));

// An unknown API path returns 404 JSON (not the React page). Any other path goes to the React client.
api.MapFallback(() => Results.Json(new { code = "not_found", error = "הכתובת לא נמצאה." }, statusCode: 404));
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
