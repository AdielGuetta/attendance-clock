using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Attendance.Api.Data;

/// <summary>Creates the database (if missing) and runs the idempotent schema script embedded in the assembly.</summary>
public static partial class DatabaseSetup
{
    public static async Task InitializeAsync(string connectionString, CancellationToken ct = default)
    {
        var settings = new SqlConnectionStringBuilder(connectionString);
        var database = settings.InitialCatalog;
        if (!SafeDatabaseName().IsMatch(database))
            throw new InvalidOperationException(
                "ConnectionStrings:Attendance must name a simple, non-system database (letters, digits, underscore).");

        var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };
        await using (var connection = new SqlConnection(master.ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using var create = connection.CreateCommand();
            // A database name cannot be a parameter. It is validated by the regex above and wrapped in QUOTENAME-style brackets.
            create.CommandText = $"IF DB_ID(@name) IS NULL CREATE DATABASE [{database}];";
            create.Parameters.AddWithValue("@name", database);
            await create.ExecuteNonQueryAsync(ct);
        }

        await using var db = new SqlConnection(connectionString);
        await db.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await db.BeginTransactionAsync(ct);
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = await ReadSchemaAsync();
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public static async Task DropAsync(string connectionString, CancellationToken ct = default)
    {
        var database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        if (!SafeDatabaseName().IsMatch(database)) throw new InvalidOperationException("Unsafe database name.");
        SqlConnection.ClearAllPools();
        var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync(ct);
        await using var drop = connection.CreateCommand();
        drop.CommandText = $"""
            IF DB_ID(@name) IS NOT NULL
            BEGIN
                ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{database}];
            END
            """;
        drop.Parameters.AddWithValue("@name", database);
        await drop.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string> ReadSchemaAsync()
    {
        await using var stream = typeof(DatabaseSetup).Assembly.GetManifestResourceStream("Attendance.Api.Database.schema.sql")
            ?? throw new InvalidOperationException("Embedded schema.sql is missing.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    [GeneratedRegex(@"^(?!(?:master|model|msdb|tempdb)$)[A-Za-z][A-Za-z0-9_]{0,99}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SafeDatabaseName();
}
