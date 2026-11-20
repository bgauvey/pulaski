#!/usr/bin/env bash
# The network-outage test (D-103): the demo runs, the edge node's link to the API is cut for OUTAGE_MINUTES (default 10) while the
# machines and the process simulator carry on, then restored; the test passes when everything the edge collected meanwhile reached
# Pulaski and nothing was lost:
#   - the cut held: the API heard nothing from the edge during the outage;
#   - the edge kept its buffer (same stream) and forwarded it all once back;
#   - each process value has readings right through the outage, never further apart than the heartbeat allows;
#   - each machine counter's counts during the outage add up exactly to how far its raw counter moved, and some were counted meanwhile.
#
# Run from the repository root: deploy/outage-test/run.sh. Needs Docker and the ports the demo uses (5080, 5090, 1883, 8474), so stop a
# running demo first. OUTAGE_MINUTES=2 for a quick try; KEEP_UP=1 leaves the stack running afterwards (docker compose -p pulaski-outage down -v).
set -euo pipefail

OUTAGE_MINUTES="${OUTAGE_MINUTES:-10}"
HEARTBEAT_SECONDS=60       # Edge:ProcessValueHeartbeat: a process value is forwarded at least this often
GAP_SLACK_SECONDS=30       # a sampling round, clock steps between containers
TOXIPROXY="http://localhost:${PULASKI_TOXIPROXY_PORT:-8474}"
COMPOSE=(docker compose -p pulaski-outage -f compose.yaml -f deploy/outage-test/compose.yaml)

sql() { "${COMPOSE[@]}" exec -T db psql -U postgres -d pulaski -v ON_ERROR_STOP=1 -tAc "$1"; }
say() { echo "[$(date -u +%H:%M:%S)] $*"; }
fail() { echo "::error::$*"; FAILED=1; }

FAILED=0
cleanup() {
  status=$?
  if [ "$status" -ne 0 ] || [ "$FAILED" -ne 0 ]; then
    "${COMPOSE[@]}" logs --no-color --tail 200 edge api toxiproxy || true
  fi
  if [ "${KEEP_UP:-0}" != 1 ]; then
    "${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

# --- Start: the proxy first, so the edge's address exists when it starts ---------------------------------------------------------
say "Starting Toxiproxy"
"${COMPOSE[@]}" up -d toxiproxy
for i in $(seq 1 30); do curl -fsS "$TOXIPROXY/version" >/dev/null 2>&1 && break; sleep 1; done
curl -fsS -X POST "$TOXIPROXY/proxies" -H 'content-type: application/json' \
  -d '{"name":"pulaski-api","listen":"0.0.0.0:8081","upstream":"api:8080","enabled":true}' >/dev/null

say "Starting the demo (images are built if needed)"
"${COMPOSE[@]}" up --build -d

for i in $(seq 1 150); do
  [ "$("${COMPOSE[@]}" ps -a --format '{{.State}}' demo-setup)" = exited ] && break
  sleep 2
done
test "$("${COMPOSE[@]}" ps -a --format '{{.ExitCode}}' demo-setup)" = 0 || { fail "The demo setup failed."; exit 1; }

# --- Steady state: the machines run and both process values arrive through the proxy ----------------------------------------------
PROCESS_TAGS="SELECT t.id FROM core.equipment_tag t WHERE t.source_address LIKE 'Plant1/ExtruderProcess/EXT-01-EXT:Process/%'"
COUNTERS="SELECT t.equipment_id FROM core.equipment_tag t JOIN core.equipment e ON e.id = t.equipment_id
          WHERE t.tag_role = 'processed_count' AND e.code IN ('EXT-01-SAW', 'PACK1')"
for i in $(seq 1 90); do
  running="$(sql "SELECT count(*) FROM core.equipment_live_state s JOIN core.equipment e ON e.id = s.equipment_id
                  WHERE e.code IN ('EXT-01-EXT', 'EXT-01-SAW', 'PACK1') AND s.packml_state = 6")"
  values="$(sql "SELECT count(DISTINCT equipment_tag_id) FROM ts.process_value WHERE equipment_tag_id IN ($PROCESS_TAGS)")"
  [ "$running" = 3 ] && [ "$values" = 2 ] && break
  sleep 2
done
[ "$running" = 3 ] && [ "$values" = 2 ] || { fail "The demo didn't reach a steady state ($running machines running, $values process values)."; exit 1; }
counters="$(sql "SELECT count(*) FROM ($COUNTERS) c")"
[ "$counters" = 2 ] || { fail "Expected the saw's and the packer's counters to be bound; found $counters."; exit 1; }

# A minute of normal running before the cut, so each series has readings to measure gaps from.
sleep 60
STREAM="$(sql "SELECT stream_id FROM core.edge_node WHERE code = 'DEMO-EDGE'")"
SEQ_BEFORE="$(sql "SELECT last_sequence FROM core.edge_node WHERE code = 'DEMO-EDGE'")"
say "Steady: edge stream $STREAM at sequence $SEQ_BEFORE"

# --- The outage: every byte between edge and API is dropped (a pulled cable, not a refused connection) ------------------------------
CUT="$(sql "SELECT now()")"
curl -fsS -X POST "$TOXIPROXY/proxies/pulaski-api/toxics" -H 'content-type: application/json' \
  -d '{"name":"outage-up","type":"timeout","stream":"upstream","toxicity":1,"attributes":{"timeout":0}}' >/dev/null
curl -fsS -X POST "$TOXIPROXY/proxies/pulaski-api/toxics" -H 'content-type: application/json' \
  -d '{"name":"outage-down","type":"timeout","stream":"downstream","toxicity":1,"attributes":{"timeout":0}}' >/dev/null
say "Cut the edge off from the API at $CUT for $OUTAGE_MINUTES minutes"
for m in $(seq 1 "$OUTAGE_MINUTES"); do
  sleep 60
  say "  $m of $OUTAGE_MINUTES minutes cut off; the API last heard from the edge at $(sql "SELECT last_seen_at FROM core.edge_node WHERE code = 'DEMO-EDGE'")"
done

# A request already on its way when the cut came may land just after it; nothing later may.
HEARD="$(sql "SELECT last_seen_at <= timestamptz '$CUT' + interval '15 seconds' FROM core.edge_node WHERE code = 'DEMO-EDGE'")"
[ "$HEARD" = t ] || fail "The API heard from the edge during the outage: the cut didn't hold."

RESTORE="$(sql "SELECT now()")"
curl -fsS -X DELETE "$TOXIPROXY/proxies/pulaski-api/toxics/outage-up" >/dev/null
curl -fsS -X DELETE "$TOXIPROXY/proxies/pulaski-api/toxics/outage-down" >/dev/null
say "Restored the link at $RESTORE; waiting for the edge's buffer to drain"

# The edge forwards its buffer in order, so readings from after the restore arriving means everything before them has arrived.
for i in $(seq 1 90); do
  caught_up="$(sql "SELECT count(*) FROM (SELECT equipment_tag_id FROM ts.process_value
                    WHERE equipment_tag_id IN ($PROCESS_TAGS) AND occurred_at > timestamptz '$RESTORE' + interval '30 seconds'
                    GROUP BY equipment_tag_id) c")"
  [ "$caught_up" = 2 ] && break
  sleep 5
done
[ "$caught_up" = 2 ] || { fail "The edge didn't catch up within 7.5 minutes of the restore."; exit 1; }
say "Caught up: the edge is at sequence $(sql "SELECT last_sequence FROM core.edge_node WHERE code = 'DEMO-EDGE'")"

# --- Nothing lost -----------------------------------------------------------------------------------------------------------------
[ "$(sql "SELECT stream_id FROM core.edge_node WHERE code = 'DEMO-EDGE'")" = "$STREAM" ] \
  || fail "The edge started a new stream: its buffer was lost."

# Process values: readings right through the outage, from the last before the cut to the first after the restore.
MAX_GAP=$((HEARTBEAT_SECONDS + GAP_SLACK_SECONDS))
while IFS='|' read -r address readings gap; do
  say "  $address: $readings readings during the outage, longest gap ${gap}s"
  [ "$readings" -ge "$OUTAGE_MINUTES" ] || fail "$address has only $readings readings over $OUTAGE_MINUTES minutes cut off."
  [ "${gap%.*}" -le "$MAX_GAP" ] || fail "$address has a gap of ${gap}s (the heartbeat allows ${MAX_GAP}s): readings were lost."
done < <(sql "
  WITH w AS (
    SELECT t.source_address, v.occurred_at,
           v.occurred_at - lag(v.occurred_at) OVER (PARTITION BY v.equipment_tag_id ORDER BY v.occurred_at) AS gap
    FROM ts.process_value v JOIN core.equipment_tag t ON t.id = v.equipment_tag_id
    WHERE v.equipment_tag_id IN ($PROCESS_TAGS)
      AND v.occurred_at BETWEEN timestamptz '$CUT' - interval '5 minutes' AND timestamptz '$RESTORE' + interval '5 minutes')
  SELECT source_address,
         count(*) FILTER (WHERE occurred_at BETWEEN timestamptz '$CUT' AND timestamptz '$RESTORE'),
         coalesce(round(extract(epoch FROM max(gap) FILTER (WHERE occurred_at > timestamptz '$CUT'
                                                          AND occurred_at - gap < timestamptz '$RESTORE'))), 0)
  FROM w GROUP BY source_address ORDER BY source_address")

# Counts: from the last count before the cut to the last one now, the deltas add up to how far the raw counter moved.
while IFS='|' read -r code rows delta moved; do
  say "  $code: $rows counts during the outage, $delta counted, raw counter moved $moved"
  [ "$rows" -gt 0 ] || fail "$code counted nothing during the outage."
  [ "$delta" = "$moved" ] || fail "$code's counts add up to $delta but its counter moved $moved: counts were lost."
done < <(sql "
  WITH c AS (
    SELECT p.equipment_id, p.occurred_at, p.delta_qty, p.raw_counter
    FROM ts.production_count p
    WHERE p.source = 'machine' AND p.count_kind = 'processed' AND p.equipment_id IN ($COUNTERS)),
  base AS (
    SELECT DISTINCT ON (equipment_id) equipment_id, occurred_at, raw_counter
    FROM c WHERE occurred_at < timestamptz '$CUT' ORDER BY equipment_id, occurred_at DESC)
  SELECT e.code,
         count(*) FILTER (WHERE c.occurred_at BETWEEN timestamptz '$CUT' AND timestamptz '$RESTORE'),
         trim_scale(sum(c.delta_qty)),
         max(c.raw_counter) - b.raw_counter
  FROM base b
  JOIN c ON c.equipment_id = b.equipment_id AND c.occurred_at > b.occurred_at
  JOIN core.equipment e ON e.id = b.equipment_id
  GROUP BY e.code, b.raw_counter ORDER BY e.code")

if [ "$FAILED" -ne 0 ]; then
  exit 1
fi
say "Passed: $OUTAGE_MINUTES minutes cut off, nothing lost."
