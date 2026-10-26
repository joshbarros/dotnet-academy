#!/usr/bin/env bash
# Drives the composed stack over HTTP. Every code carries a run id, so a kept Postgres volume never collides.
# Needs: curl, python3. The stack must be up: docker compose up --build --wait
set -euo pipefail

API="${API:-http://127.0.0.1:5214}"
RABBIT="${RABBIT:-http://127.0.0.1:15704}"
RUN="$(date +%s)$((RANDOM % 900 + 100))"
JSON='content-type: application/json'

say() { printf '  %s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }
jget() { python3 -c "import sys,json; d=json.load(sys.stdin); print(d$1)"; }

open_cohort() { # code seatCapacity -> prints nothing
  curl -sf -X POST "$API/api/cohorts" -H "$JSON" \
    -d "{\"code\":\"$1\",\"title\":\"Smoke Course\",\"track\":\"Engineering\",\"instructor\":\"Ada Lovelace\",\"seatCapacity\":$2}" >/dev/null \
    || fail "could not open cohort $1"
}
book() { # code reference seats -> prints enrollment id
  curl -sf -X POST "$API/api/enrollments" -H "$JSON" \
    -d "{\"cohortCode\":\"$1\",\"reference\":\"$2\",\"seats\":$3}" | jget "['id']"
}
enrollment_field() { curl -sf "$API/api/enrollments/$1" | jget "['$2']"; }
remaining() { curl -sf "$API/api/cohorts/$1" | jget "['seatsLeft']"; }

wait_for() { # description command... : polls up to 30s until the command succeeds
  local what="$1"; shift
  for _ in $(seq 1 60); do
    if "$@" >/dev/null 2>&1; then return 0; fi
    sleep 0.5
  done
  fail "timed out waiting for $what"
}
status_is() { [ "$(enrollment_field "$1" status)" = "$2" ]; }
remaining_is() { [ "$(remaining "$1")" = "$2" ]; }
terminal() { case "$(enrollment_field "$1" status)" in Enrolled|Rejected|Withdrawn) return 0 ;; *) return 1 ;; esac; }
event_types() { curl -sf "$API/api/enrollments/$1/events" | python3 -c "import sys,json; print(' '.join(e['type'] for e in json.load(sys.stdin)))"; }
dlq_is() { [ "$(dlq_count)" = "$1" ]; }
dlq_count() { curl -sf -u platform:platform "$RABBIT/api/queues/%2F/passes.work.dlq" | jget "['messages']"; }

curl -sf "$API/api/health" >/dev/null || fail "API is not healthy at $API; run: docker compose up --build --wait"

echo "1. input is validated at the door"
open_cohort "V-$RUN" 10
for body in \
  "{\"cohortCode\":\"V-$RUN\",\"reference\":\"\",\"seats\":1}" \
  "{\"cohortCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"seats\":0}" \
  "{\"cohortCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"seats\":6}" \
  "{\"cohortCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"seats\":4294967297}" \
  "{\"cohortCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"seats\":9223372036854775807}"; do
  code="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API/api/enrollments" -H "$JSON" -d "$body")"
  [ "$code" = 400 ] || fail "expected 400 for $body, got $code"
done
say "empty, zero, over-limit and overflowing seat counts are all 400"

echo "2. happy path"
open_cohort "H-$RUN" 10
ID="$(book "H-$RUN" "LRN-$RUN-1" 3)"
wait_for "enrollment to be Enrolled" status_is "$ID" Enrolled
[ "$(enrollment_field "$ID" feeCents)" = 15200 ] || fail "fee should be 3 x 4900 + 500 cents"
[ "$(curl -sf "$API/api/passes/$ID" | jget "['status']")" = Issued ] || fail "access pass not issued"
wait_for "remaining seats to be 7" remaining_is "H-$RUN" 7
TYPES="$(event_types "$ID")"
for t in seats.reserve seats.held pass.issued; do
  case "$TYPES" in *"$t"*) ;; *) fail "Kafka log is missing $t (has: $TYPES)" ;; esac
done
say "enrolled, fee 15200, remaining 7, Kafka log has the three steps"

echo "3. last seat: three enrollments, one commits, two are short and write no hold"
open_cohort "L-$RUN" 1
TMP="$(mktemp -d)"
for n in A B C; do ( book "L-$RUN" "LRN-$RUN-$n" 1 >"$TMP/$n" ) & done
wait
IDS=""
for n in A B C; do [ -s "$TMP/$n" ] || fail "enrollment $n was not accepted"; IDS="$IDS $(cat "$TMP/$n")"; done
rm -rf "$TMP"
for i in $IDS; do wait_for "enrollment $i to settle" terminal "$i"; done
ENROLLED=0; SHORT=0
for i in $IDS; do
  s="$(enrollment_field "$i" status)"
  if [ "$s" = Enrolled ]; then ENROLLED=$((ENROLLED + 1)); fi
  if [ "$s" = Rejected ]; then
    case "$(enrollment_field "$i" reason)" in Short*) SHORT=$((SHORT + 1)); case "$(event_types "$i")" in *seats.held*) fail "the short loser has a hold" ;; esac ;; esac
  fi
done
[ "$ENROLLED" = 1 ] && [ "$SHORT" = 2 ] || fail "expected 1 enrolled and 2 short, got $ENROLLED enrolled and $SHORT short"
[ "$(remaining "L-$RUN")" = 0 ] || fail "remaining should be 0"
say "1 enrolled, 2 short, remaining 0, no hold for the losers"

echo "4. poison: committed as rejected, dead-lettered, the seats come back"
open_cohort "P-$RUN" 5
BEFORE="$(dlq_count)"
IDP="$(book "P-$RUN" "LRN-POISON-$RUN" 2)"
wait_for "poison enrollment to be Rejected" status_is "$IDP" Rejected
wait_for "seats to be restored to 5" remaining_is "P-$RUN" 5
wait_for "passes.work.dlq to gain one message" dlq_is $((BEFORE + 1))
[ "$(curl -sf "$API/api/passes/$IDP" | jget "['status']")" = Rejected ] || fail "rejection should be committed on the pass row"
[ "$(enrollment_field "$IDP" seatsReleasedAt)" != None ] || fail "enrollment should record the release"
say "rejected, dead-lettered (+1 in passes.work.dlq), remaining back to 5"

echo "5. withdrawal races the final mark: the withdrawal wins, the issued pass stays"
open_cohort "R-$RUN" 6
WON=0
for attempt in 1 2 3; do
  IDR="$(book "R-$RUN" "LRN-$RUN-R$attempt" 2)"
  # The access pass exists before the enrollment is marked Enrolled (the final mark is delayed in compose).
  wait_for "access pass to exist" curl -sf "$API/api/passes/$IDR"
  if status_is "$IDR" Held; then WON=1; break; fi
  say "window missed on attempt $attempt (enrollment already Enrolled); the withdrawal still has to hold; retrying"
  curl -sf -X POST "$API/api/enrollments/$IDR/withdrawal" >/dev/null || true
  wait_for "enrollment to be Withdrawn" status_is "$IDR" Withdrawn
done
[ "$WON" = 1 ] || fail "never caught the enrollment between pass issued and final mark"
curl -sf -X POST "$API/api/enrollments/$IDR/withdrawal" >/dev/null || fail "withdrawal was refused"
wait_for "enrollment to be Withdrawn" status_is "$IDR" Withdrawn
sleep 3 # longer than the final-mark delay: the late mark must not undo the withdrawal
status_is "$IDR" Withdrawn || fail "the final mark overwrote the withdrawal"
[ "$(curl -sf "$API/api/passes/$IDR" | jget "['status']")" = Issued ] || fail "the issued pass was rolled back"
[ "$(enrollment_field "$IDR" passNumber)" = None ] || fail "the late final mark should have been skipped"
wait_for "seats to come back" remaining_is "R-$RUN" 6
code="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API/api/enrollments/$IDR/withdrawal")"
[ "$code" = 409 ] || fail "second withdrawal should be 409, got $code"
say "enrollment Withdrawn, pass still Issued, remaining 6, second withdrawal 409"

echo "academy smoke passed"
