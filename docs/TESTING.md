# Test log

## Summary: what passed and what still needs attention

| Area | How it was tested | Status |
|------|-------------------|--------|
| .NET server build | `dotnet build` locally (.NET SDK 10.0.404), warnings as errors | ✅ passes |
| React build + TypeScript | `npm run build` locally (Vite 8, TS 7) | ✅ passes |
| Schema, constraints, locks in SQL Server | `scripts/verify-sql.ps1` against local SQL Server 2022 Express | ✅ 13/13 |
| External time APIs | `curl` to timezone.io and timeapi.io from the development machine | ✅ both respond |
| UI end to end (browser) | Vite + `scripts/mock-api.mjs` (real external time, same API contract) | ✅ 8 scenarios, 1 bug found and fixed |
| .NET unit tests (time, DST, report) | GitHub Actions | ✅ pass (run [#4](https://github.com/AdielGuetta/attendance-clock/actions/runs/44305968065), after fixes 9-10) |
| .NET integration tests (API + real SQL) | GitHub Actions with SQL Server 2022 | ✅ pass, plus init-db twice and a smoke test of the real server against the real time service |
| Requirements review (plan 3) | Tracing both punches from the button to `Shifts`/`Punches` | ✅ the time always comes from `IExternalTimeService`. 1 gap found and fixed (FIXES #11) |
| Full system in Docker from a clean checkout, following the README | CI job "Full system in Docker": `cp .env.example .env` → `docker compose up --build` → `scripts/demo-check.sh` | ✅ [run](https://github.com/AdielGuetta/attendance-clock/actions/runs/44511675010): real .NET server, SQL Server 2022, real time service (`worldtimeapi`) |
| Demo flow in a real browser against the full system | CI: `scripts/browser-demo.mjs` (Playwright, headless Chromium) | ✅ clock in → refresh (timer 00:00:00 → 00:00:02) → clock out → the shift in the manager report |
| Time-service outage on the full system | CI: `compose.outage.yaml` | ✅ 503 `time_unavailable`, nothing saved, and the same requestId succeeds after recovery |
| Data survives a server restart | CI: `docker compose restart app` | ✅ |
| The full system on **this** Windows machine | Clean clone → README | ⚠️ **blocked at `docker compose`**: WSL 2 and Docker Desktop are not installed. Installing them needs administrator rights and a restart, so the user does it. Every step before that works |
| The real .NET server directly on Windows (without Docker) | — | ❌ blocked by Smart App Control, which the user chose to keep on. Not needed with Docker |

**What still needs attention:**
1. ~~Push to GitHub and confirm the CI run is green~~ ✅ done on 2026-11-18 (4 runs, fixes 9-10).
2. **On the presentation machine:** install WSL 2 + Docker Desktop once (README, section "הרצה מהירה"), then `docker compose up --build -d --wait` and walk through the demo at http://localhost:8080.
   Until then, the browser run on the full system is verified only in CI (Linux), not on this Windows machine.

## Run log

| Date | What ran | Result |
|------|----------|--------|
| 2026-11-17 | `dotnet build backend/Attendance.sln` | ✅ after fixes 1-2 in FIXES.md |
| 2026-11-17 | `dotnet test --solution backend/Attendance.sln` | ❌ `0x800711C7 Application Control policy has blocked this file`: environment block, not a code failure |
| 2026-11-17 | `dotnet backend/.../Attendance.Api.dll` | ❌ same block. Smart App Control confirmed via `VerifiedAndReputablePolicyState=1` |
| 2026-11-17 | `scripts/verify-sql.ps1` (SQL Express) | ❌ PowerShell parse error (fix 6) → ✅ 13/13 |
| 2026-11-17 | `npm run build` | ✅ |
| 2026-11-17 | Browser E2E against mock-api | ✅ 7/8 on the first round; scenario 4 failed → fix 8 → ✅ 8/8 |
| 2026-11-18 | First GitHub Actions run (#44304856012) | Frontend ✅. Backend: build ✅, `dotnet test` ❌ exit code 5 before any test ran (fix 9) |
| 2026-11-18 | GitHub Actions #2-#3 | Tests ran. Everything passed except one integration test (fix 10). Added a step that publishes failed tests as annotations, since logs are visible only when signed in |
| 2026-11-18 | GitHub Actions #4 | ✅ all green: build, all tests (unit + integration against SQL Server 2022), init-db twice, smoke test of the real server against the real time service, React build |
| 2026-11-18 | Plan 3 review + fix 11. CI on c57a8fe | ✅ green, including the new overlap test |
| 2026-11-19 | CI on 238982d: new Docker job (clean checkout → compose → `demo-check.sh`) | ✅ green on the first try |
| 2026-11-19 | CI on ae2ca58: added the browser check | ❌ Playwright 1.56 failed to install on the Ubuntu 26 runner (fix 12). The API check ✅ |
| 2026-11-19 | CI on 986074f ([run](https://github.com/AdielGuetta/attendance-clock/actions/runs/44511675010)) | ✅ all 3 jobs: tests, React build, Docker (API flow, browser flow, restart, outage) |
| 2026-11-19 | This machine: clean clone of the GitHub repository + README steps | ✅ clone (15 commits) and `.env`. ❌ `docker: command not found`, and `wsl --status` reports WSL is not installed. Intel i5-1335U with a hypervisor already running (so WSL 2 should work), 24 GB RAM |

## Database checks (verify-sql.ps1): actual output

```
PASS  schema.sql runs
PASS  schema.sql runs again without errors or duplicates (idempotent)
PASS  Hebrew names stored as nvarchar
PASS  second open shift for the same employee is rejected (UX_Shifts_OneOpenPerEmployee)
PASS  open shift for a different employee is allowed
PASS  non-UTC offset is rejected (CK_Shifts_StartIsUtc)
PASS  end before start is rejected (CK_Shifts_Order)
PASS  end without source is rejected (CK_Shifts_EndSource)
PASS  same RequestId twice is rejected (PK_Punches = idempotency key)
PASS  second clock-in on the same shift is rejected (UX_Punches_ShiftAction)
PASS  invalid action is rejected (CK_Punches_Action)
PASS  UPDLOCK,HOLDLOCK on employee row blocks a concurrent punch transaction
PASS  report overlap query finds night shift on both Zurich days around fall-back
13 passed, 0 failed
```

## Browser scenarios (2026-11-17)

Environment: Vite dev server on 5173 → proxy → `mock-api.mjs` on 5080 → worldtime.timezone.io.

| # | Scenario | Steps | Expected | Result |
|---|----------|-------|----------|--------|
| 1 | Double/triple click | Three `click()` calls in a row on the button | One request to the server | ✅ the mock log shows one request |
| 2 | Refresh during an open shift | Clock-in, then reload the page | The timer continues from the clock-in time on the server | ✅ the timer showed 00:00:12 right after the refresh |
| 3 | Time service outage + retry | Inject `timeFailure`, click, restore, click again | A clear error, nothing saved, the "Try again" button, **the same requestId** on retry | ✅ the log shows the same requestId twice; one record |
| 4 | Refresh in the middle of sending | 8-second delay in the time service, click, reload after 0.3 s | "Checking…" with the button locked, then "the punch from before the refresh was saved" | ❌ first round: "not completed" while the shift had closed on the server → **fix 8** → ✅ |
| 5 | Sync between tabs | Two tabs; clock-in in one | The second tab updates without a refresh | ✅ (BroadcastChannel) |
| 6 | Stale screen | Close the shift through the API "from another device", then click clock-out on the stale screen | 409 with a message + the screen refreshes to "not on shift" | ✅ "No open shift to close. It may have been closed in another window." |
| 7 | Manager report | Today / week / day breakdown / CSV | Every employee (including zero hours), totals, Sunday to Saturday, CSV with offset and source | ✅ |
| 8 | Phone width | 375px viewport | No horizontal scrolling of the page, full-width button | ✅ |

DST scenarios are not covered by the browser run (they would need a fake clock). They are covered by:
`ZurichTimeTests`, `ReportBuilderTests`, `TimeSourceTests` (the repeated autumn hour in timeapi.io),
`PunchApiTests.Night_shift_across_fall_back_is_reported_per_zurich_day`, and the last check in `verify-sql.ps1`.

## The automated tests (run in CI)

**Unit** (`backend/Attendance.Tests/Unit`, no database needed):
- `ZurichTimeTests`: a 23-hour day (2026-03-29), a 25-hour day (2026-10-25), the spring gap, the repeated autumn hour resolved by the flag, a contradicting flag.
- `TimeSourceTests`: a real sample from timezone.io, 7 invalid responses (wrong zone, non-UTC offset, missing field, a mismatch between `datetime` and `utc_datetime`, an implausible year, an array), broken JSON, HTTP 503, timeapi.io conversion including the repeated hour and the gap.
- `ExternalTimeServiceTests`: a healthy primary provider, a transient error → retry → fallback, invalid data → no retry, timeout, every provider failing → 503, user cancellation is not reported as an outage.
- `ReportBuilderTests`: splitting a night shift, a full 25-hour day, a shift across the spring gap (2 real hours), an open shift counted up to "now", clamping to the range, range validation.

**Integration** (`backend/Attendance.Tests/Integration`, `ATTENDANCE_TEST_SQL` required):
clock-in/out with an external time and its source; an outage → 503 with no write; a double click with the same requestId at the same moment;
5 concurrent requests → exactly one succeeds; a retry after a lost response, including during an outage; reusing an id for a different action;
a stale screen (`shift_changed`); 409 without calling the time service; clock-out earlier than clock-in; invalid input;
a night shift across the autumn clock change (9 real hours: 2 on Saturday, 7 on Sunday); an open shift after a refresh and in the report.

### How to run

```powershell
dotnet test --solution backend/Attendance.sln                       # unit only
$env:ATTENDANCE_TEST_SQL = "Server=localhost\SQLEXPRESS;Integrated Security=True;Encrypt=True;TrustServerCertificate=True"
dotnet test --solution backend/Attendance.sln                       # + integration (creates and drops AttendanceTest_xxx)
powershell -ExecutionPolicy Bypass -File scripts/verify-sql.ps1      # database checks without .NET
```
