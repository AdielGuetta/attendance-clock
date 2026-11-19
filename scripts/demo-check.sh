#!/usr/bin/env bash
# End-to-end check of a running system (docker compose up): the same flow as the demo, through the HTTP API.
# clock-in → "refresh" (reload the state) → clock-out → the shift is in the manager report.
# The real .NET server, the real SQL Server and the real external time service. Requires curl and jq.
#   bash scripts/demo-check.sh [base-url] [employee-id]
set -euo pipefail
BASE=${1:-http://localhost:8080}
EMP=${2:-1}
uuid() { cat /proc/sys/kernel/random/uuid 2>/dev/null || uuidgen; }
step() { printf '\n== %s\n' "$*"; }
# In GitHub Actions, ::notice lines become public annotations on the run page (the log itself needs sign-in).
note() { [ -n "${GITHUB_ACTIONS:-}" ] && echo "::notice title=api: $1::$2" || true; }

step "Waiting for $BASE/api/health"
for i in $(seq 1 90); do curl -sf "$BASE/api/health" >/dev/null && break; sleep 2; [ "$i" = 90 ] && { echo "server did not come up"; exit 1; }; done

step "The React page is served by the server"
curl -sf "$BASE/" | grep -q '<div id="root">'
echo ok

step "Close a shift left open by an earlier run (so the check can be repeated)"
OPEN=$(curl -sf "$BASE/api/employees/$EMP/attendance" | jq -r '.openShift.id // empty')
if [ -n "$OPEN" ]; then
  curl -sf -X POST "$BASE/api/employees/$EMP/clock-out" -H 'Content-Type: application/json' \
    -d "{\"requestId\":\"$(uuid)\",\"shiftId\":\"$OPEN\"}" >/dev/null
  echo "closed $OPEN"
else
  echo "none"
fi

step "Clock in"
IN_REQ=$(uuid)
IN=$(curl -sf -X POST "$BASE/api/employees/$EMP/clock-in" -H 'Content-Type: application/json' -d "{\"requestId\":\"$IN_REQ\"}")
echo "$IN" | jq -c '{shiftId, atZurich, timeSource}'
note clock-in "$(echo "$IN" | jq -c '{atZurich, timeSource}')"
SHIFT=$(echo "$IN" | jq -r .shiftId)
[[ "$(echo "$IN" | jq -r .timeSource)" =~ ^(worldtimeapi|timeapi.io)$ ]] || { echo "unexpected time source"; exit 1; }

step "Double click: the same requestId again returns the same receipt"
AGAIN=$(curl -sf -X POST "$BASE/api/employees/$EMP/clock-in" -H 'Content-Type: application/json' -d "{\"requestId\":\"$IN_REQ\"}")
[ "$(echo "$AGAIN" | jq -r .shiftId)" = "$SHIFT" ] && [ "$(echo "$AGAIN" | jq -r .replayed)" = true ]
echo ok

step "Refresh: the open shift comes back from the server with the same clock-in time"
STATE=$(curl -sf "$BASE/api/employees/$EMP/attendance")
[ "$(echo "$STATE" | jq -r .openShift.id)" = "$SHIFT" ]
echo "$STATE" | jq -c '{openShift: .openShift.startZurich, serverNowUtc}'

sleep 2
step "Clock out"
OUT=$(curl -sf -X POST "$BASE/api/employees/$EMP/clock-out" -H 'Content-Type: application/json' \
  -d "{\"requestId\":\"$(uuid)\",\"shiftId\":\"$SHIFT\"}")
echo "$OUT" | jq -c '{atZurich, timeSource}'

step "The shift is in today's manager report"
REPORT=$(curl -sf "$BASE/api/report")
ROW=$(echo "$REPORT" | jq -c --arg id "$SHIFT" '.shifts[] | select(.id == $id) | {employeeName, startZurich, endZurich, durationSeconds, startSource, endSource}')
echo "$ROW"
note report "$ROW"
[ "$(echo "$REPORT" | jq --arg id "$SHIFT" '[.shifts[] | select(.id == $id and .endUtc != null)] | length')" = 1 ]

step "All checks passed"
