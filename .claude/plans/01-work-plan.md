# Work plan: attendance-claude

Written before implementation. It is updated at the end of each stage with the actual status.
Status values: ⬜ not started · 🟡 in progress · ✅ done · ⚠️ done with a limitation.

## Goal

A submission version of an attendance clock system:
ASP.NET Core (.NET 10) + Microsoft SQL Server + React.
An employee clocks in and out and sees their shifts. A manager sees an attendance report.
The time of every new punch comes from an external API for `Europe/Zurich`.

## What I took from the reference project (attendance-reference, built with Codex)

Requirements and decisions I keep:
- Each punch carries a client-generated `requestId` (UUID) → idempotency against double clicks and retries.
- Clock-out names the `shiftId` the employee saw → a stale screen cannot close a new shift.
- Times are stored in UTC (`datetimeoffset`), and report boundaries are computed by Zurich midnight with TimeZoneInfo.
- The external time is fetched **before** the SQL transaction opens, so no locks are held during a network call.
- A filtered unique index enforces one open shift per employee at the database level.
- No local clock is used when the time service fails → the punch is rejected with a clear message.

What I improve or change:
1. **Fallback time provider**: a primary provider (timezone.io, WorldTimeAPI format) plus a second provider (timeapi.io).
   The second one returns local time without an offset → convert with `dstActive` and validate against the zone rules
   (a time inside the spring-forward gap is rejected; the repeated autumn hour is resolved by `dstActive`).
2. **Short retry** per provider, a timeout per attempt, and a total time budget.
3. **Audit trail**: every punch stores the time source (`TimeSource`) and the Zurich offset at that moment.
4. **Report with totals**: hours per employee per Zurich day, split exactly at midnight (a night shift is split between two days),
   so totals can be summed correctly. This fixes a known limitation of the reference project.
5. **Page refresh during a punch**: the pending `requestId` is kept in `localStorage`. After a refresh the client asks
   the server whether that punch was saved (`GET .../punches/{requestId}`) and shows the result. It does not send it again automatically.
6. **Sync between tabs**: BroadcastChannel + refresh on window focus, so an old tab does not show a stale state.
7. **Server-side local times**: the API returns Zurich local time with an explicit offset (`+01:00`/`+02:00`),
   so the display does not depend on the browser's time zone and the repeated autumn hour is distinguishable.
8. **Automated tests in .NET**: xUnit unit tests (parsing, fallback, DST, day splitting) +
   integration tests with WebApplicationFactory against a real SQL Server and a controllable time provider.

## Structure

```
attendance-claude/
  backend/Attendance.Api/        ASP.NET Core minimal API
  backend/Attendance.Tests/      xUnit: unit + integration
  frontend/                      React + Vite + TypeScript
  database/schema.sql            idempotent schema script (also embedded in the server)
  docs/                          test log, fix log, presentation
  .claude/                       instructions and plans used during development
  CLAUDE.md, README.md, DECISIONS.md
```

## Stages (each stage = at least one commit)

| # | Stage | Done when | Status |
|---|-------|-----------|--------|
| 0 | Plan, CLAUDE.md, .claude, skeleton | The plan is committed | ✅ |
| 1 | Install tools (.NET 10, Node LTS, SQL Server Express) | `dotnet`, `node`, SQL respond | ⬜ |
| 2 | Schema + DB setup (`--init-db`) | Running it twice creates tables idempotently | ⬜ |
| 3 | Time service: two providers, retry, validation | Unit tests pass | ⬜ |
| 4 | Punch and shift API with locking and idempotency | Integration tests pass | ⬜ |
| 5 | Manager report with day splitting and DST | Report tests pass | ⬜ |
| 6 | React: employee screen, history, refresh, tabs | Build passes, manual check | ⬜ |
| 7 | React: manager report + CSV | Manual check | ⬜ |
| 8 | End-to-end check (browser + real time API + SQL) | Results recorded in docs/TESTING.md | ⬜ |
| 9 | Documentation: README, DECISIONS, presentation, GitHub | Files are complete | ⬜ |

## Edge cases to cover (checklist)

- [ ] Time service: HTTP 5xx, timeout, invalid JSON, wrong zone, timestamp without UTC → fallback → 503 with no write.
- [ ] Double click: the button is disabled + the same `requestId` → one record.
- [ ] Concurrent requests with different `requestId`s → one succeeds and one gets 409.
- [ ] Retry after a lost response → the original receipt, with no call to the time service.
- [ ] Reusing a `requestId` for a different action → 409.
- [ ] Clock-out with a stale `shiftId` → 409 `shift_changed`.
- [ ] Clock-out time earlier than clock-in (provider clock skew) → 409.
- [ ] Refresh during an open shift → the timer continues from the clock-in time stored on the server.
- [ ] Refresh in the middle of sending → server check by `requestId`.
- [ ] Spring forward (23-hour day) and fall back (25-hour day): correct duration and day boundaries.
- [ ] Night shift across midnight → split between two days in the totals.
- [ ] Shift still open → shown in the report as "open", counted up to the report time (or up to the end of the day).
