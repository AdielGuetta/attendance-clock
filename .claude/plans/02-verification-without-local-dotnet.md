# Plan B: verification when .NET cannot run locally

Written mid-work (2026-11-17) after `dotnet test` and `dotnet Attendance.Api.dll` failed with
`0x800711C7 An Application Control policy has blocked this file`.

## Diagnosis
- `HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy\VerifiedAndReputablePolicyState = 1` → Smart App Control in enforce mode.
- It blocks unsigned DLLs built locally. Signed tools (node, npm, SQL Server, dotnet.exe itself) work.
- No WSL or Docker. PowerShell is in FullLanguage mode.

## Options presented to the user
1. The user turns off Smart App Control → full verification locally. (On some versions it cannot be turned back on without a reset.)
2. Do not turn it off; verify by other means. ← **the user's choice**

I do not bypass the protection (for example by loading assemblies from memory). It is a security setting, and the decision belongs to the user.

## Plan B stages
| Stage | What it covers | Result |
|-------|----------------|--------|
| GitHub Actions: build + all tests against SQL Server 2022 + init-db twice + smoke against the real time service | All .NET code | Ready. Runs on the first push |
| `scripts/verify-sql.ps1` against local SQL Express | Schema, idempotency, constraints, UPDLOCK, a DST night shift | 13/13 ✅ |
| `scripts/mock-api.mjs` + browser | UI flows: double click, refresh, outage, tabs, stale screen, report | 8/8 ✅ after a fix |
| Thorough code review of the server instead of running it | Bugs that only a run would reveal | Found fixes 4 and 5 |

## Explicitly not covered locally
- The real .NET server against SQL Express (only in CI, on Linux).
- The UI against the real server (the mock implements the same contract, but it is not the same code).
