#!/usr/bin/env bash
# End-to-end test: drives the running stack through the full admin → owner → proposal flow,
# hitting the real Vocdoni SaaS API with the token from .env. Assertions fail the run (exit 1).
#
# Usage:  ./e2e.sh            (starts the stack if needed, leaves it running)
#         BASE=http://host:port ./e2e.sh
set -uo pipefail

BASE="${BASE:-http://localhost:5095}"
[ -f .env ] && { set -a; . ./.env; set +a; }
ADMIN_EMAIL="${ADMIN_EMAIL:-admin@hoa.local}"
ADMIN_PASSWORD="${ADMIN_PASSWORD:-change-me}"

pass=0; fail=0
ok()  { printf "  \033[32m✓\033[0m %s\n" "$1"; pass=$((pass+1)); }
bad() { printf "  \033[31m✗\033[0m %s\n" "$1"; fail=$((fail+1)); }
step(){ printf "\n\033[1m▶ %s\033[0m\n" "$1"; }
die() { printf "\n\033[31mABORT:\033[0m %s\n" "$1"; summary; exit 1; }

# req METHOD PATH [json] [bearer] -> sets HTTP, BODY
req() {
  local method="$1" path="$2" data="${3:-}" auth="${4:-}" out
  local args=(-s -m 90 -X "$method" "$BASE$path" -H 'Content-Type: application/json' -w $'\n%{http_code}')
  [ -n "$data" ] && args+=(-d "$data")
  [ -n "$auth" ] && args+=(-H "Authorization: Bearer $auth")
  out=$(curl "${args[@]}")
  HTTP="${out##*$'\n'}"; BODY="${out%$'\n'*}"
}
j() { printf '%s' "$1" | jq -r "$2" 2>/dev/null; }

summary() { printf "\n\033[1m── %d passed, %d failed ──\033[0m\n" "$pass" "$fail"; }

# --- ensure the stack is up ------------------------------------------------
if [ "$(curl -s -m 3 -o /dev/null -w '%{http_code}' "$BASE/api/auth/login" -X POST -H 'Content-Type: application/json' -d '{}' 2>/dev/null)" = "000" ]; then
  echo "Stack not reachable at $BASE — starting docker compose..."
  docker compose up -d --build >/dev/null 2>&1 || die "docker compose up failed"
  for i in $(seq 1 60); do
    [ "$(curl -s -m 3 -o /dev/null -w '%{http_code}' "$BASE/api/auth/login" -X POST -H 'Content-Type: application/json' -d '{}' 2>/dev/null)" != "000" ] && break
    sleep 1
  done
fi

OWNER_EMAIL="owner+$(date +%s)@e2e.local"
OWNER_PW="owner-pw-123"
ASSOC_NAME="E2E Homeowners Voting Platform $(date +%H%M%S)"
CSV="${CSV:-memberbase-test.csv}"   # memberbase: First Name,Email,Member Number
VTYPE="${VTYPE:-single}"            # voting type: single | multiple | ranked

# --- 1. admin login --------------------------------------------------------
step "Admin login ($ADMIN_EMAIL)"
req POST /api/auth/login "{\"email\":\"$ADMIN_EMAIL\",\"password\":\"$ADMIN_PASSWORD\"}"
[ "$HTTP" = 200 ] && ok "200" || die "admin login HTTP $HTTP — $BODY (check ADMIN_* in .env)"
ADMIN_TOKEN=$(j "$BODY" .token); [ -n "$ADMIN_TOKEN" ] && ok "received JWT" || die "no token in response"

# --- 2. negative: unauthenticated create must be 401 -----------------------
step "Guard: unauthenticated create → 401"
req POST /api/associations "{\"name\":\"x\",\"ownerEmail\":\"x@x\",\"ownerPassword\":\"x\"}"
[ "$HTTP" = 401 ] && ok "401" || bad "expected 401, got $HTTP"

# --- 3. find-or-create association (integrator free plan caps managed orgs, so reuse) ------
# This e2e owns its test associations and always uses the same owner password, so a reused
# association can be logged into. A fresh account creates one; later runs reuse it.
step "Find or create association"
req GET /api/associations "" "$ADMIN_TOKEN"
[ "$HTTP" = 200 ] || die "list associations HTTP $HTTP — $BODY"
ASSOC_ID=$(printf '%s' "$BODY" | jq -r '.[0].id // empty')
if [ -n "$ASSOC_ID" ]; then
  OWNER_EMAIL=$(printf '%s' "$BODY" | jq -r '.[0].ownerEmail')
  ORG=$(printf '%s' "$BODY" | jq -r '.[0].vocdoniOrgAddress')
  ok "reusing association id=$ASSOC_ID (owner=$OWNER_EMAIL, org=$ORG)"
else
  req POST /api/associations "{\"name\":\"$ASSOC_NAME\",\"ownerEmail\":\"$OWNER_EMAIL\",\"ownerPassword\":\"$OWNER_PW\"}" "$ADMIN_TOKEN"
  if [ "$HTTP" = 201 ]; then
    ASSOC_ID=$(j "$BODY" .id); ORG=$(j "$BODY" .vocdoniOrgAddress)
    ok "created association id=$ASSOC_ID, vocdoni org=$ORG"
  elif printf '%s' "$BODY" | grep -q 40154; then
    # Integrator managed-org quota is full — adopt an existing managed org instead.
    ORG=$(curl -s -m 15 "$VOCDONI_SERVER_URL/integrator/organizations" \
      -H "Authorization: Bearer $VOCDONI_API_TOKEN" | jq -r '.organizations[0].address // empty')
    [ -n "$ORG" ] || die "managed-org quota full and no existing org to adopt"
    OWNER_EMAIL="owner-import@e2e.local"
    req POST /api/associations/import \
      "{\"name\":\"$ASSOC_NAME\",\"vocdoniOrgAddress\":\"$ORG\",\"ownerEmail\":\"$OWNER_EMAIL\",\"ownerPassword\":\"$OWNER_PW\"}" "$ADMIN_TOKEN"
    [ "$HTTP" = 201 ] && { ASSOC_ID=$(j "$BODY" .id); ok "quota full → adopted existing org=$ORG as association id=$ASSOC_ID"; } \
      || die "import association HTTP $HTTP — $BODY"
  else
    die "create association HTTP $HTTP — $BODY"
  fi
fi

# --- 4. owner login --------------------------------------------------------
step "Owner login ($OWNER_EMAIL)"
req POST /api/auth/login "{\"email\":\"$OWNER_EMAIL\",\"password\":\"$OWNER_PW\"}"
[ "$HTTP" = 200 ] && ok "200" || die "owner login HTTP $HTTP — $BODY"
OWNER_TOKEN=$(j "$BODY" .token); [ -n "$OWNER_TOKEN" ] && ok "received JWT" || die "no token"

# --- 5. negative: owner cannot read a non-owned/missing association --------
step "Guard: owner accessing missing association 999999 → 404"
req GET /api/associations/999999 "" "$OWNER_TOKEN"
[ "$HTTP" = 404 ] && ok "404" || bad "expected 404, got $HTTP"

# --- 6. owner loads the memberbase from CSV (Vocdoni: add members) ----------
# Idempotent: only load if the org has no members yet. Re-adding would create duplicate
# memberNumbers, which an auth-only (no-2FA) census rejects (its credential is hash(memberNumber)).
step "Load memberbase from $CSV"
[ -f "$CSV" ] || die "memberbase CSV not found: $CSV"
req GET "/api/associations/$ASSOC_ID/homeowners" "" "$OWNER_TOKEN"
EXISTING=$(printf '%s' "$BODY" | jq 'length' 2>/dev/null)
if [ "${EXISTING:-0}" -gt 0 ]; then
  ok "memberbase already present (count=$EXISTING), skipping load"
else
  rows=0; added=0
  # Handles both "First Name,Member Number" and "First Name,Email,Member Number".
  while IFS= read -r line; do
    line="${line//$'\r'/}"; [ -z "$line" ] && continue
    IFS=, read -ra col <<< "$line"
    name="${col[0]}"
    if [ "${#col[@]}" -ge 3 ]; then email="${col[1]}"; memnum="${col[2]}"; else email=""; memnum="${col[1]}"; fi
    [ -z "$name" ] && continue
    rows=$((rows + 1))
    emailJson=""; [ -n "$email" ] && emailJson="\"email\":\"$email\","
    req POST "/api/associations/$ASSOC_ID/homeowners" \
      "{\"name\":\"$name\",${emailJson}\"memberNumber\":\"$memnum\",\"weight\":\"1\"}" "$OWNER_TOKEN"
    if [ "$HTTP" = 200 ]; then added=$((added + $(j "$BODY" '.added // 0'))); else bad "add '$name' (#$memnum) HTTP $HTTP — $BODY"; fi
  done < <(tail -n +2 "$CSV")
  [ "$rows" -gt 0 ] && ok "$rows CSV rows processed, $added newly added" || die "no rows parsed from $CSV"
fi

# --- 7. owner lists homeowners ---------------------------------------------
step "List homeowners"
req GET "/api/associations/$ASSOC_ID/homeowners" "" "$OWNER_TOKEN"
COUNT=$(printf '%s' "$BODY" | jq 'length' 2>/dev/null)
[ "$HTTP" = 200 ] && ok "200, count=$COUNT" || bad "list HTTP $HTTP — $BODY"

# --- 8. owner creates a multi-question proposal (Vocdoni #571: authored + batch-published) ------
# Publish is an async on-chain batch (one election per question); this step waits for it (~10-30s).
START=$(date -u +%Y-%m-%dT%H:%M:%SZ)
END=$(date -u -v+7d +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u -d '+7 days' +%Y-%m-%dT%H:%M:%SZ)
step "Create proposal (2 questions: single + multiple; waits for batch publish)"
req POST "/api/associations/$ASSOC_ID/proposals" \
  "{\"title\":\"Building decisions\",\"description\":\"E2E proposal\",\"startDate\":\"$START\",\"endDate\":\"$END\",\"questions\":[{\"title\":\"Repaint the lobby?\",\"kind\":\"single\",\"choices\":[{\"title\":\"Yes\"},{\"title\":\"No\"}]},{\"title\":\"Which upgrades?\",\"kind\":\"multiple\",\"choices\":[{\"title\":\"Elevator\"},{\"title\":\"Garden\"},{\"title\":\"Gym\"}]}]}" "$OWNER_TOKEN"
if [ "$HTTP" = 201 ]; then
  PROP_ID=$(j "$BODY" .id); PROC=$(j "$BODY" .vocdoniProcessId)
  QN=$(printf '%s' "$BODY" | jq '.questions | length'); UP=$(printf '%s' "$BODY" | jq -r '.questions[0].upstreamId')
  ok "201 — proposal id=$PROP_ID, process=$PROC, $QN questions, q1 upstreamId=${UP:0:12}…"
  [ -n "$UP" ] && [ "$UP" != "null" ] && ok "questions published (have on-chain ids)" || bad "questions missing upstreamId — batch publish incomplete"
else
  bad "create proposal HTTP $HTTP — $BODY"; PROP_ID=""
fi

# --- 9. read + close (only if proposal created) ----------------------------
# Voting is client-side (per-question CSP sign + relay); per-question tallies come inline on the
# process read once a question reaches "results" status (saas-backend #596).
if [ -n "${PROP_ID:-}" ]; then
  step "Read proposal (questions + on-chain status)"
  req GET "/api/associations/$ASSOC_ID/proposals/$PROP_ID" "" "$OWNER_TOKEN"
  [ "$HTTP" = 200 ] && ok "200 (q1 status=$(j "$BODY" '.questions[0].status'))" || bad "get HTTP $HTTP — $BODY"

  step "Close proposal (ends every question)"
  req POST "/api/associations/$ASSOC_ID/proposals/$PROP_ID/close" "" "$OWNER_TOKEN"
  # Close enqueues the on-chain end and returns 202; the status reconciles on the next read.
  [ "$HTTP" = 202 ] && ok "202 (close enqueued)" || bad "close HTTP $HTTP — $BODY"
fi

summary
[ "$fail" -eq 0 ]
