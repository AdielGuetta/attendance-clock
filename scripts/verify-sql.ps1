<#
.SYNOPSIS
  Checks the schema and database guarantees directly against SQL Server, without building the .NET project.
  Written because Smart App Control on the development machine blocks running locally built DLLs.
  Creates a disposable database, checks, and drops it at the end.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts/verify-sql.ps1
  powershell -ExecutionPolicy Bypass -File scripts/verify-sql.ps1 -Server "localhost,1433" -SqlAuth "sa:MyPassword"
#>
param(
  [string]$Server = "localhost\SQLEXPRESS",
  [string]$SqlAuth = ""
)
$ErrorActionPreference = "Stop"
$db = "AttendanceSqlCheck_" + [guid]::NewGuid().ToString("N").Substring(0, 8)
$auth = if ($SqlAuth) { $u, $p = $SqlAuth -split ":", 2; "User ID=$u;Password=$p" } else { "Integrated Security=True" }
$base = "Server=$Server;$auth;Encrypt=True;TrustServerCertificate=True;Application Name=verify-sql"
$schema = Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot "..\backend\Attendance.Api\Database\schema.sql")

$script:passed = 0; $script:failed = 0
function Open([string]$database) {
  $c = New-Object System.Data.SqlClient.SqlConnection "$base;Database=$database"
  $c.Open(); return $c
}
function Exec($conn, [string]$sql, $tx = $null) {
  $cmd = $conn.CreateCommand(); $cmd.CommandText = $sql; $cmd.Transaction = $tx; $cmd.CommandTimeout = 30
  return $cmd.ExecuteScalar()
}
function Check([string]$name, [scriptblock]$body) {
  try { & $body; Write-Host "PASS  $name" -ForegroundColor Green; $script:passed++ }
  catch { Write-Host "FAIL  $name :: $($_.Exception.Message)" -ForegroundColor Red; $script:failed++ }
}
function ExpectSqlError([int[]]$numbers, [scriptblock]$body) {
  try { & $body } catch {
    $inner = $_.Exception
    while ($inner -and -not ($inner -is [System.Data.SqlClient.SqlException])) { $inner = $inner.InnerException }
    if ($inner -and $numbers -contains $inner.Number) { return }
    throw "expected SQL error $($numbers -join '/'), got: $($_.Exception.Message)"
  }
  throw "expected SQL error $($numbers -join '/'), but the statement succeeded"
}

$master = Open "master"
Exec $master "CREATE DATABASE [$db];" | Out-Null
Write-Host "Disposable database: $db on $Server"
try {
  $c = Open $db

  Check "schema.sql runs" { Exec $c $schema | Out-Null }
  Check "schema.sql runs again without errors or duplicates (idempotent)" {
    Exec $c $schema | Out-Null
    $n = Exec $c "SELECT COUNT(*) FROM dbo.Employees"
    if ($n -ne 3) { throw "expected 3 seed employees, got $n" }
  }
  Check "Hebrew names stored as nvarchar" {
    # Windows PowerShell 5.1 reads a BOM-less script as ANSI, so the expected name is built from code points (Noa Cohen in Hebrew).
    $expected = -join [char[]](0x05E0, 0x05D5, 0x05E2, 0x05D4, 0x20, 0x05DB, 0x05D4, 0x05DF)
    $name = Exec $c "SELECT FullName FROM dbo.Employees WHERE Id = 1"
    if ($name -ne $expected) { throw "got '$name'" }
  }

  $s1 = [guid]::NewGuid()
  Exec $c "INSERT dbo.Shifts (Id, EmployeeId, StartUtc, StartSource) VALUES ('$s1', 1, '2026-10-24T20:00:00+00:00', 'test')" | Out-Null

  Check "second open shift for the same employee is rejected (UX_Shifts_OneOpenPerEmployee)" {
    ExpectSqlError 2601 { Exec $c "INSERT dbo.Shifts (Id, EmployeeId, StartUtc, StartSource) VALUES (NEWID(), 1, '2026-10-24T21:00:00+00:00', 'test')" }
  }
  Check "open shift for a different employee is allowed" {
    Exec $c "INSERT dbo.Shifts (Id, EmployeeId, StartUtc, StartSource) VALUES (NEWID(), 2, '2026-10-24T21:00:00+00:00', 'test')" | Out-Null
  }
  Check "non-UTC offset is rejected (CK_Shifts_StartIsUtc)" {
    ExpectSqlError 547 { Exec $c "INSERT dbo.Shifts (Id, EmployeeId, StartUtc, StartSource) VALUES (NEWID(), 3, '2026-10-24T22:00:00+02:00', 'test')" }
  }
  Check "end before start is rejected (CK_Shifts_Order)" {
    ExpectSqlError 547 { Exec $c "UPDATE dbo.Shifts SET EndUtc = '2026-10-24T19:00:00+00:00', EndSource = 'test' WHERE Id = '$s1'" }
  }
  Check "end without source is rejected (CK_Shifts_EndSource)" {
    ExpectSqlError 547 { Exec $c "UPDATE dbo.Shifts SET EndUtc = '2026-10-25T05:00:00+00:00' WHERE Id = '$s1'" }
  }

  $req = [guid]::NewGuid()
  Exec $c "INSERT dbo.Punches (EmployeeId, RequestId, ShiftId, Action, AtUtc, TimeSource, ZurichOffsetMinutes) VALUES (1, '$req', '$s1', 'in', '2026-10-24T20:00:00+00:00', 'test', 120)" | Out-Null
  Check "same RequestId twice is rejected (PK_Punches = idempotency key)" {
    ExpectSqlError 2627 { Exec $c "INSERT dbo.Punches (EmployeeId, RequestId, ShiftId, Action, AtUtc, TimeSource, ZurichOffsetMinutes) VALUES (1, '$req', '$s1', 'out', '2026-10-25T05:00:00+00:00', 'test', 60)" }
  }
  Check "second clock-in on the same shift is rejected (UX_Punches_ShiftAction)" {
    ExpectSqlError 2601 { Exec $c "INSERT dbo.Punches (EmployeeId, RequestId, ShiftId, Action, AtUtc, TimeSource, ZurichOffsetMinutes) VALUES (1, NEWID(), '$s1', 'in', '2026-10-24T20:00:01+00:00', 'test', 120)" }
  }
  Check "invalid action is rejected (CK_Punches_Action)" {
    ExpectSqlError 547 { Exec $c "INSERT dbo.Punches (EmployeeId, RequestId, ShiftId, Action, AtUtc, TimeSource, ZurichOffsetMinutes) VALUES (1, NEWID(), '$s1', 'xx', '2026-10-24T20:00:01+00:00', 'test', 120)" }
  }

  Check "UPDLOCK,HOLDLOCK on employee row blocks a concurrent punch transaction" {
    $a = Open $db; $b = Open $db
    try {
      $txA = $a.BeginTransaction()
      Exec $a "SELECT Id FROM dbo.Employees WITH (UPDLOCK, HOLDLOCK) WHERE Id = 1" $txA | Out-Null
      Exec $b "SET LOCK_TIMEOUT 1500" | Out-Null
      $txB = $b.BeginTransaction()
      ExpectSqlError 1222 { Exec $b "SELECT Id FROM dbo.Employees WITH (UPDLOCK, HOLDLOCK) WHERE Id = 1" $txB }
      $txB.Rollback(); $txA.Rollback()
      # A different employee is not blocked
      $txA = $a.BeginTransaction()
      Exec $a "SELECT Id FROM dbo.Employees WITH (UPDLOCK, HOLDLOCK) WHERE Id = 1" $txA | Out-Null
      $txB = $b.BeginTransaction()
      Exec $b "SELECT Id FROM dbo.Employees WITH (UPDLOCK, HOLDLOCK) WHERE Id = 2" $txB | Out-Null
      $txB.Rollback(); $txA.Rollback()
    } finally { $a.Close(); $b.Close() }
  }

  Check "report overlap query finds night shift on both Zurich days around fall-back" {
    Exec $c "UPDATE dbo.Shifts SET EndUtc = '2026-10-25T05:00:00+00:00', EndSource = 'test' WHERE Id = '$s1'" | Out-Null
    # Zurich midnights: 24.10 = 23.10T22:00Z, 25.10 = 24.10T22:00Z, 26.10 = 25.10T23:00Z (a 25-hour day)
    $q = "SELECT COUNT(*) FROM dbo.Shifts WHERE Id = '$s1' AND StartUtc < @end AND (EndUtc IS NULL OR EndUtc > @start)"
    foreach ($case in @(@('2026-10-23T22:00:00+00:00', '2026-10-24T22:00:00+00:00', 1), @('2026-10-24T22:00:00+00:00', '2026-10-25T23:00:00+00:00', 1), @('2026-10-25T23:00:00+00:00', '2026-10-26T23:00:00+00:00', 0))) {
      $n = Exec $c ($q.Replace('@start', "'$($case[0])'").Replace('@end', "'$($case[1])'"))
      if ($n -ne $case[2]) { throw "range $($case[0])..$($case[1]) expected $($case[2]) got $n" }
    }
    $minutes = Exec $c "SELECT DATEDIFF(MINUTE, StartUtc, EndUtc) FROM dbo.Shifts WHERE Id = '$s1'"
    if ($minutes -ne 540) { throw "expected 540 minutes (9 real hours), got $minutes" }
  }

  $c.Close()
}
finally {
  [System.Data.SqlClient.SqlConnection]::ClearAllPools()
  Exec $master "ALTER DATABASE [$db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$db];" | Out-Null
  $master.Close()
}
Write-Host ""
Write-Host "$script:passed passed, $script:failed failed"
if ($script:failed -gt 0) { exit 1 }
