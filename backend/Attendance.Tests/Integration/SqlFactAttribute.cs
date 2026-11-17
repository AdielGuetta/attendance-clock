using System.Runtime.CompilerServices;

namespace Attendance.Tests.Integration;

/// <summary>A test that needs SQL Server. Skipped (not failed) when ATTENDANCE_TEST_SQL is not set.</summary>
public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (AttendanceApiFactory.ServerConnection is null)
            Skip = $"Set {AttendanceApiFactory.EnvVar} to run SQL Server integration tests.";
    }
}
