using Attendance.Api.Data;
using Attendance.Api.Domain;
using Attendance.Api.Time;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Attendance.Tests.Integration;

/// <summary>
/// Runs the real application (TestServer) against a real SQL Server.
/// Each run creates a disposable database named AttendanceTest_xxx and drops it at the end.
/// The external time service is replaced by <see cref="FakeExternalTime"/> so tests can control the time and failures.
/// Requires the environment variable ATTENDANCE_TEST_SQL (a connection string without Database). Without it the tests are skipped.
/// </summary>
public sealed class AttendanceApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string EnvVar = "ATTENDANCE_TEST_SQL";

    public static readonly string? ServerConnection = Environment.GetEnvironmentVariable(EnvVar);

    public FakeExternalTime Time { get; } = new();
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
    public string ConnectionString { get; }

    private int _nextEmployeeId = 100;

    public AttendanceApiFactory()
    {
        var builder = new SqlConnectionStringBuilder(ServerConnection ?? "Server=unused")
        {
            InitialCatalog = "AttendanceTest_" + Guid.NewGuid().ToString("N")[..12]
        };
        ConnectionString = builder.ConnectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development"); // InitializeOnStartup=true → the schema is created automatically
        builder.UseSetting("ConnectionStrings:Attendance", ConnectionString);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IExternalTimeService>();
            services.AddSingleton<IExternalTimeService>(Time);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    public ValueTask InitializeAsync()
    {
        if (ServerConnection is not null)
            _ = Server; // starts the server → creates the database
        return ValueTask.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (ServerConnection is not null)
            await DatabaseSetup.DropAsync(ConnectionString);
    }

    /// <summary>Creates a fresh employee so tests do not depend on one another.</summary>
    public async Task<int> CreateEmployeeAsync(string name = "Test Employee")
    {
        var id = Interlocked.Increment(ref _nextEmployeeId);
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("INSERT INTO dbo.Employees (Id, FullName) VALUES (@id, @name);", connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@name", name + " " + id);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    public async Task<int> CountAsync(string table, int employeeId)
    {
        if (table is not ("Shifts" or "Punches")) throw new ArgumentException("Unknown table", nameof(table));
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"SELECT COUNT(*) FROM dbo.{table} WHERE EmployeeId = @id;", connection);
        command.Parameters.AddWithValue("@id", employeeId);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}

/// <summary>A controllable time service: the next time, an injected failure, a delay (to create overlap), and a call counter.</summary>
public sealed class FakeExternalTime : IExternalTimeService
{
    private readonly Lock _gate = new();
    private DateTimeOffset _now = new(2026, 11, 17, 7, 0, 0, TimeSpan.Zero);
    private int _calls;

    public bool Fail { get; set; }
    public TimeSpan Delay { get; set; }
    public int Calls => Volatile.Read(ref _calls);

    public void Set(DateTimeOffset utc) { lock (_gate) _now = utc; }

    public async Task<ExternalTime> GetCurrentAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
        if (Fail)
            throw new AttendanceException(503, "time_unavailable", "fake outage");
        lock (_gate) return new ExternalTime(_now, "fake");
    }
}
