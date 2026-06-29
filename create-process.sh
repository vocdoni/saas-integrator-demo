#!/usr/bin/env bash
# Create a new voting process on an EXISTING census — no org/owner/member setup.
# Assumes the integrator account + its managed org + a published census already exist.
#
# Talks directly to the Vocdoni SaaS API with the integrator token from .env. ORG and CENSUS_ID
# come from env vars; if omitted, they are auto-discovered from the running app (first
# association + its latest proposal's census).
#
# Usage:
#   ORG=0x.. CENSUS_ID=6a.. ./create-process.sh        # explicit
#   ./create-process.sh                                  # discover from the app
#   TITLE="Budget 2026" ./create-process.sh
set -uo pipefail

[ -f .env ] && { set -a; . ./.env; set +a; }
: "${VOCDONI_BASE_URL:?set VOCDONI_BASE_URL in .env}"
: "${VOCDONI_API_TOKEN:?set VOCDONI_API_TOKEN in .env}"
B="$VOCDONI_BASE_URL"; H="Authorization: Bearer $VOCDONI_API_TOKEN"; CT="Content-Type: application/json"
APP="${APP:-http://localhost:5095}"
TITLE="${TITLE:-Reused-census process}"

# --- resolve ORG + CENSUS_ID (env, else discover from the app) --------------
if [ -z "${ORG:-}" ] || [ -z "${CENSUS_ID:-}" ]; then
  echo "Discovering ORG/CENSUS_ID from the app at $APP ..."
  AT=$(curl -s -X POST "$APP/api/auth/login" -H "$CT" \
        -d "{\"email\":\"${ADMIN_EMAIL:-}\",\"password\":\"${ADMIN_PASSWORD:-}\"}" | jq -r '.token // empty')
  [ -n "$AT" ] || { echo "Cannot reach/login to app; pass ORG and CENSUS_ID explicitly."; exit 1; }
  ASSOC=$(curl -s "$APP/api/associations" -H "Authorization: Bearer $AT")
  AID=$(echo "$ASSOC" | jq -r '.[0].id // empty')
  : "${ORG:=$(echo "$ASSOC" | jq -r '.[0].vocdoniOrgAddress // empty')}"
  if [ -z "${CENSUS_ID:-}" ] && [ -n "$AID" ]; then
    CENSUS_ID=$(curl -s "$APP/api/associations/$AID/proposals" -H "Authorization: Bearer $AT" \
                  | jq -r '[.[].vocdoniCensusId] | last // empty')
  fi
fi
[ -n "${ORG:-}" ] && [ -n "${CENSUS_ID:-}" ] || {
  echo "Need ORG and CENSUS_ID (set them, or create a proposal first so they can be discovered)."; exit 1; }

# --- verify the census exists (GET /census doesn't always echo published.root,
#     so the real test is whether POST /process accepts it below) --------------
CENSUS=$(curl -s -m 15 "$B/census/$CENSUS_ID" -H "$H")
SIZE=$(echo "$CENSUS" | jq -r '.size // 0')
CTYPE=$(echo "$CENSUS" | jq -r '.type // empty')
[ -n "$CTYPE" ] || { echo "census $CENSUS_ID not found: $CENSUS"; exit 1; }
[ "$SIZE" -gt 0 ] || { echo "census $CENSUS_ID has no participants (size=0) — publish it first"; exit 1; }
echo "org    = $ORG"
echo "census = $CENSUS_ID  (type=$CTYPE, size=$SIZE)"

# --- create the process on that census --------------------------------------
NOW=$(date -u +%Y-%m-%dT%H:%M:%SZ)
END=$(date -u -v+7d +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u -d '+7 days' +%Y-%m-%dT%H:%M:%SZ)
MAXSIZE=$(( SIZE > 0 ? SIZE : 1000 ))
BODY=$(cat <<JSON
{"orgAddress":"$ORG","censusId":"$CENSUS_ID","metadata":{"title":"$TITLE"},
 "electionParams":{"title":{"default":"$TITLE"},"description":{"default":"created by create-process.sh"},
  "questions":[{"title":{"default":"$TITLE"},"choices":[{"title":{"default":"Yes"},"value":0},{"title":{"default":"No"},"value":1}]}],
  "voteType":{"maxCount":1,"maxValue":1},"electionType":{"autostart":true,"interruptible":true},
  "startDate":"$NOW","endDate":"$END","maxCensusSize":$MAXSIZE}}
JSON
)
PROCESS=$(curl -s -m 30 -X POST "$B/process" -H "$H" -H "$CT" -d "$BODY" | tr -d '"')
case "$PROCESS" in
  ""|*error*|*'{'*) echo "create process failed: $PROCESS"; exit 1 ;;
esac
echo "process = $PROCESS"

# --- publish (async) and poll until the on-chain election id is assigned -----
curl -s -m 30 -o /dev/null -X POST "$B/process/$PROCESS/publish" -H "$H"
ONCHAIN=""
for i in $(seq 1 20); do
  ONCHAIN=$(curl -s -m 15 "$B/process/$PROCESS" -H "$H" | jq -r '.address // empty')
  [ -n "$ONCHAIN" ] && break
  sleep 2
done
[ -n "$ONCHAIN" ] || { echo "process did not get an on-chain id within the timeout"; exit 1; }

echo ""
echo "✓ process created & published on the existing census"
echo "  process id (status/results) : $PROCESS"
echo "  on-chain election id        : $ONCHAIN  (voting flow only)"
echo "  results                     : GET $B/process/$PROCESS/results"
