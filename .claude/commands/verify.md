---
description: Run the project's full check (build, tests, frontend build) and record the result
---

Run in order and stop at the first failure:

1. `dotnet build backend/Attendance.sln -warnaserror`
2. `dotnet test backend/Attendance.sln` (if `ATTENDANCE_TEST_SQL` is set, the integration tests run too)
3. `npm --prefix frontend run build`

Then add a row to `docs/TESTING.md` with the date, what ran, how many tests passed or were skipped, and failures.
If something failed and you fixed it, record it in `docs/FIXES.md` as well.
