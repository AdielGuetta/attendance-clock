# Plan 3: requirements review, demo run on Windows, presentation prep

Written on 2026-11-18, at the start of the second work round (after CI went green).
Status values: ⬜ not started · 🟡 in progress · ✅ done · ⚠️ done with a limitation · ❌ blocked.

## Requirements review (against the original task)

| Requirement | Where it is | Verified how | Status |
|-------------|-------------|--------------|--------|
| Backend in ASP.NET Core | `backend/Attendance.Api` (`Microsoft.NET.Sdk.Web`, net10.0, minimal API) | Build + tests in CI | ✅ |
| Microsoft SQL Server | `Microsoft.Data.SqlClient`, `Database/schema.sql` | Integration tests against SQL Server 2022 in CI, `verify-sql.ps1` against SQL Express | ✅ |
| Frontend in React | `frontend/` (React 19, Vite, TypeScript) | Build in CI, browser scenarios | ✅ |
| Employee clocks in and out and sees their shifts | `EmployeeClock.tsx`, `ShiftTable.tsx`, `/clock-in`, `/clock-out`, `/attendance` | Integration tests, browser | ✅ |
| Manager attendance report | `ManagerReport.tsx`, `/api/report`, `ReportBuilder` | `ReportBuilderTests`, integration test, browser | ✅ |
| The time of every new punch comes from an external API for `Europe/Zurich` | Traced below | Code trace + integration tests with a fake provider + CI smoke test against the real provider | ✅ |
| Time service failure | `ExternalTimeService` (fallback, retry, 503) | `ExternalTimeServiceTests`, integration test, browser | ✅ |
| Double clicks | `useRef` lock + `requestId` | Integration test, browser | ✅ |
| Concurrent requests | `UPDLOCK, HOLDLOCK` + unique indexes | Integration test with 5 parallel requests, `verify-sql.ps1` | ✅ |
| Refresh during an open shift | State from the server, `localStorage` for a pending punch | Integration test, browser | ✅ |
| DST transitions | UTC storage, `ZurichTime`, day split by Zurich midnight | Unit tests, integration test of a night shift | ✅ |
| Provider time cannot open a shift before the previous one ended | `EnsureAfterPreviousShift` | New integration test | ❌ → ✅ fixed in this round (FIXES #11) |
| Plan in a file, commits per stage | `.claude/plans/`, git history | — | ✅ |
| README, DECISIONS, CLAUDE.md, `.claude` | repository root | Package check (stage 6) | ✅ |
| Test and fix log | `docs/TESTING.md`, `docs/FIXES.md` | — | ✅ |
| End-to-end check of the full system | — | Until now: browser against the mock only | ⚠️ → stage 3 |

### Tracing both punches to the database
1. `EmployeeClock.tsx` → `api.clockIn` / `api.clockOut` (`frontend/src/api.ts`) send `POST` with a `requestId` (and the `shiftId` for clock-out). **The client sends no time at all.**
2. `Program.cs` → `AttendanceRepository.PunchAsync`.
3. `PunchAsync` calls `time.GetCurrentAsync` (`IExternalTimeService`) → `ExternalTimeService` → `WorldTimeApiSource` (`worldtime.timezone.io/api/timezone/Europe/Zurich`) or `TimeApiIoSource` (`timeapi.io/...?timeZone=Europe/Zurich`).
4. The same `now.UtcNow` value is written to `Shifts.StartUtc` (clock-in) or `Shifts.EndUtc` (clock-out), and to `Punches.AtUtc`, with the source in `StartSource`/`EndSource`/`TimeSource`.
5. The server clock (`TimeProvider`) appears only in `serverNowUtc` (the timer), in the "today" date and in the estimate of an open shift in the report. SQL's `SYSUTCDATETIME()` appears only in `Punches.RecordedAtUtc` (audit).

### Found in the review
- **Overlapping shifts:** clock-out was checked against the clock-in, but clock-in was not checked against the end of the previous shift. Two providers a few seconds apart could open a shift that starts before the previous one ended. Fixed under the employee lock (`time_before_previous_end`).
- **`RecordedAtUtc`** was not explained. Documented in `DECISIONS.md` and in `schema.sql`.
- `CLAUDE.md` named the provider classes `...Provider`; the real names are `...Source`. Fixed.

## Stages of this round

| # | Stage | Done when | Status |
|---|-------|-----------|--------|
| 1 | Requirements review + overlap fix + `RecordedAtUtc` docs | Test written, build passes, CI green | ✅ CI green on c57a8fe |
| 2 | Check the Windows machine, prepare a Docker run of the full system (SQL Server + .NET server + React) | `docker compose up` brings up everything | ✅ Dockerfile + compose. No WSL/Docker on this machine: the user installs them (admin + restart) |
| 3 | Clean clone → README → browser: clock in, refresh, clock out, report, with the real server, SQL Server and the real time service | Recorded in `docs/TESTING.md` | ⚠️ passes in CI (Linux, clean checkout, headless Chromium). On this Windows machine: blocked at `docker compose` until Docker is installed |
| 4 | A Hebrew guide for a non-programmer (`docs/GUIDE.md`) | — | ✅ |
| 5 | `docs/PRESENTATION.md` for a 10-minute practice + a small manual change + a change with Claude Code (`docs/PRACTICE.md`) | — | ✅ plus a live outage demo (`compose.outage.yaml`, checked in CI) |
| 6 | Submission package check + CI green on the latest commit | — | ✅ clean clone has every file, no secrets. CI: see `docs/TESTING.md` |

## Deliberately left for later (not in this round)
- Login and permissions (an employee sees only themselves, the manager screen only for managers).
- Manager corrections of punches (a separate table with who corrected and why, without changing the original punch).
- Automatic close or alert for a forgotten clock-out.
- Employee management screen (add/deactivate), history paging beyond 30 shifts.
- A migrations tool instead of the idempotent script.
- A paid time provider or an internal NTP server with an SLA.

## Decisions made with the user in this round
- Keep Smart App Control on → the demo runs in Docker (Linux VM), not as a locally built Windows .NET app.
- The app UI, README and DECISIONS stay in Hebrew; the new guide is in Hebrew too (the user's answer when asked about "English interface").
- Installing WSL 2 / Docker Desktop changes Windows features and needs admin + restart, so the user does it; Claude Code did not.
