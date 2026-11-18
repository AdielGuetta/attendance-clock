# Fix log

Every bug or problem found during development and testing: symptom → cause → fix → what covers it from now on.

| # | Date | Symptom | Cause | Fix | Coverage |
|---|------|---------|-------|-----|----------|
| 1 | 2026-11-17 | Build error in a test: `cannot convert from 'double' to 'DateTime'` | `Assert.Equal(8.5 * 3600, long?)` picked the wrong overload | Explicit cast to `long` | Compiler |
| 2 | 2026-11-17 | `Microsoft.AspNetCore.Mvc.Testing` would not install (NU1202) | The latest version (11.0.0) targets net11 only | Pinned to `10.0.15`, matching net10 (LTS) | `dotnet restore` |
| 3 | 2026-11-17 | `dotnet test` failed: "VSTest target is no longer supported" | xUnit v3 + .NET 10 SDK require Microsoft.Testing.Platform | Added `global.json` with `"test": {"runner": "Microsoft.Testing.Platform"}` and the command `dotnet test --solution` | CI |
| 4 | 2026-11-17 | The connection string set by the test host could be missed | `Program.cs` read `GetConnectionString` before `Build()`, before WebApplicationFactory adds its settings | The connection string is read from `IConfiguration` at use time (after Build), and the test also sets `Database:InitializeOnStartup` | Integration tests in CI |
| 5 | 2026-11-17 | An unknown API path (`/api/xyz`) would have returned the React page with 200 | `MapFallbackToFile` caught every path | Added `api.MapFallback` that returns 404 JSON | Code review |
| 6 | 2026-11-17 | `verify-sql.ps1` failed to parse in Windows PowerShell 5.1 | A BOM-less UTF-8 file is read as ANSI, which broke the Hebrew string | The expected string is built from Unicode code points | `scripts/verify-sql.ps1` run |
| 7 | 2026-11-17 | The open-shift date was compared against a UTC date (in the UI) | `toISOString()` returns a UTC date, not a Zurich date | Compare with `zurichToday(now)` | Code review, browser check |
| 8 | 2026-11-17 | After a refresh in the middle of a clock-out, the screen showed "the punch did not complete" and an open shift, while the shift was already closed on the server | The check after the refresh (`GET punches/{requestId}`) ran before the original request finished (it was still waiting for the time service), and a 404 was treated as final | While the punch is younger than 30 s (more than the client timeout), keep checking every 2 s with the button locked. Then refresh the state | Browser scenario 4 in TESTING.md |
| 9 | 2026-11-18 | First CI run: `dotnet test` exited with code 5 and no test ran (no TestResults folder) | In Microsoft.Testing.Platform, code 5 means an unknown command-line option. `--report-trx` needs the `Microsoft.Testing.Extensions.TrxReport` package, which isn't in the project | xUnit v3 has its own TRX report: switched to `--report-xunit-trx` | The second CI run |
| 10 | 2026-11-18 | Third CI run: everything passed except `Open_shift_survives_refresh_and_shows_in_report` with `Cannot go back in time` | The fake server clock (`FakeTimeProvider`) starts at 12:00 and the test set it back to 09:15 with `SetUtcNow`, which allows only forward moves. A test bug, not a code bug | `AdjustTime`, which allows setting any time | The test itself in CI |
| 11 | 2026-11-18 | Requirements review: a clock-in could start before the previous shift ended | Clock-out was checked against the clock-in (`time_before_start`), but clock-in was not checked against the previous `EndUtc`. Two providers a few seconds apart could open overlapping shifts, and the overlap would be counted twice in the report | Inside the transaction, under the employee lock: `MAX(EndUtc)` for the employee, and a time earlier than it → 409 `time_before_previous_end`. Same second is allowed. Also added to `mock-api.mjs` | `PunchApiTests.Clock_in_earlier_than_previous_clock_out_is_rejected` |

## Environment limitation (not a code bug)

**Smart App Control** is enabled on the development machine (`VerifiedAndReputablePolicyState=1`).
It blocks loading any DLL built locally (`0x800711C7: An Application Control policy has blocked this file`),
so the .NET server and tests cannot run on this machine. The user decided not to turn the setting off.
Fallbacks: (1) GitHub Actions runs all the .NET tests against SQL Server 2022 on Linux;
(2) `scripts/verify-sql.ps1` checks the schema and locks against the local SQL Server Express;
(3) a browser end-to-end check against `scripts/mock-api.mjs` (a Node stand-in for the API, which calls the real time service).
