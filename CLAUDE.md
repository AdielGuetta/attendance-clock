# CLAUDE.md: instructions for working on the project

This file guides Claude Code (and any developer) working in this repository.

## About the project
An attendance clock: ASP.NET Core (.NET 10) minimal API + Microsoft SQL Server + React (Vite, TypeScript).
The time of every new punch comes **only** from an external API for `Europe/Zurich`. Never from the server's clock.

## Structure
- `backend/Attendance.Api/` server. Main files:
  - `Time/` time providers (`WorldTimeApiSource`, `TimeApiIoSource` in `TimeSources.cs`) and the fallback chain `ExternalTimeService`.
  - `Data/AttendanceRepository.cs` all SQL, parameterized only.
  - `Domain/` models, `ZurichTime` (zone rules), `ReportBuilder` (splitting shifts into days).
  - `Database/schema.sql` idempotent schema script, embedded as a resource.
- `backend/Attendance.Tests/` xUnit. `Unit/` needs no database. `Integration/` needs `ATTENDANCE_TEST_SQL`.
- `frontend/` React. `src/api.ts` server calls, `src/components/` screens.
- `docs/` test log (`TESTING.md`), fix log (`FIXES.md`), presentation (`PRESENTATION.md`).
- `.claude/plans/` work plans. Update the status table at the end of each stage.

## Commands
```powershell
dotnet build backend/Attendance.sln
dotnet test --solution backend/Attendance.sln      # integration tests are skipped without ATTENDANCE_TEST_SQL
powershell -ExecutionPolicy Bypass -File scripts/verify-sql.ps1   # database checks without .NET
node scripts/mock-api.mjs                          # API stand-in for UI testing only
dotnet run --project backend/Attendance.Api -- --init-db
dotnet run --project backend/Attendance.Api        # http://localhost:5080
npm --prefix frontend run dev                      # http://localhost:5173 with a proxy to /api
npm --prefix frontend run build                    # output to backend/Attendance.Api/wwwroot
```

## Rules that must not be broken
1. **The punch time comes from `IExternalTimeService` only.** Do not use `DateTime.Now`/`UtcNow` to determine a punch time.
   The server clock may be used only for display (the date of the "today" report, the open-shift timer).
2. **Do not hold a SQL transaction open during a network call.** Fetch the time first, then open the transaction.
3. **Every punch is idempotent by `(EmployeeId, RequestId)`.** A retry with the same id returns the original receipt.
4. **Times are stored in UTC** (`datetimeoffset(7)` with a +00:00 offset). Conversion to Zurich time happens in `ZurichTime` only.
5. **Duration = the difference between UTC instants.** Never subtract local times (DST breaks that).
6. Parameterized SQL only. No string concatenation of values.
7. User-facing error messages are in Hebrew, with a stable English `code` for the client and tests.

## Workflow
- Small stages, one commit per stage, with a message that describes what was actually done.
- Every bug found during testing is recorded in `docs/FIXES.md` (symptom, cause, fix, test that covers it).
- Every test run is recorded in `docs/TESTING.md` with the date and the result.
- Before a commit: `dotnet test` and `npm --prefix frontend run build` must pass.

## Known environment on the development machine
Smart App Control is on and blocks running locally built .NET DLLs. Do not try to bypass it.
The .NET tests run in GitHub Actions. Locally: `verify-sql.ps1` and `mock-api.mjs` + browser. Details in `.claude/plans/02-verification-without-local-dotnet.md`.
