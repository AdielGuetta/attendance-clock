# Test log

## Summary: what passed and what still needs attention

| Area | How it was tested | Status |
|------|-------------------|--------|
| .NET server build | `dotnet build` locally (.NET SDK 10.0.404), warnings as errors | ✅ passes |
| React build + TypeScript | `npm run build` locally (Vite 8, TS 7) | ✅ passes |
| Schema, constraints, locks in SQL Server | `scripts/verify-sql.ps1` against local SQL Server 2022 Express | ✅ 13/13 |
| External time APIs | `curl` to timezone.io and timeapi.io from the development machine | ✅ both respond |
| UI end to end (browser) | Vite + `scripts/mock-api.mjs` (real external time, same API contract) | ✅ 8 scenarios, 1 bug found and fixed |
| .NET unit tests (time, DST, report) | GitHub Actions | ⚠️ **written and compile, but not yet run**: they run on the first push to GitHub |
| .NET integration tests (API + real SQL) | GitHub Actions with SQL Server 2022 | ⚠️ same as above |
| The real .NET server against SQL Express on this machine | — | ❌ **blocked**: Smart App Control prevents loading locally built DLLs (see FIXES.md) |

**What still needs attention:**
1. Push to GitHub and confirm the CI run is green (backend + frontend). If a test fails, fix it and record it in FIXES.md.
2. On a machine without Smart App Control (or with it turned off): run the real server against SQL Express and repeat the browser scenarios below against it instead of the mock.

## Run log

| Date | What ran | Result |
|------|----------|--------|
| 2026-11-17 | `dotnet build backend/Attendance.sln` | ✅ after fixes 1-2 in FIXES.md |
| 2026-11-17 | `dotnet test --solution backend/Attendance.sln` | ❌ `0x800711C7 Application Control policy has blocked this file`: environment block, not a code failure |
| 2026-11-17 | `dotnet backend/.../Attendance.Api.dll` | ❌ same block. Smart App Control confirmed via `VerifiedAndReputablePolicyState=1` |
| 2026-11-17 | `scripts/verify-sql.ps1` (SQL Express) | ❌ PowerShell parse error (fix 6) → ✅ 13/13 |
| 2026-11-17 | `npm run build` | ✅ |
| 2026-11-17 | Browser E2E against mock-api | ✅ 7/8 on the first round; scenario 4 failed → fix 8 → ✅ 8/8 |

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
