#!/usr/bin/env bash
# Beat 2 of the demo: two unlike engines, one identical API contract.
#
#   docker compose -f docker-compose.minimal.yml -f docker-compose.demo.yml up -d --build
#   ./demo.sh
set -euo pipefail
API=http://localhost:8080
EMAIL=admin@example.com
PASS='a-strong-password-1'

say() { printf '\n\033[1;36m== %s\033[0m\n' "$*"; }

say "Wait for the platform"
for i in $(seq 1 60); do curl -fsS "$API/health" >/dev/null 2>&1 && break || sleep 2; done

say "First-run setup (ignore 409 if already done)"
curl -fsS -X POST "$API/system/setup" -H 'Content-Type: application/json' \
  -d "{\"email\":\"$EMAIL\",\"displayName\":\"Admin\",\"password\":\"$PASS\"}" >/dev/null 2>&1 || true

say "Log in"
TOKEN=$(curl -fsS -X POST "$API/system/auth/login" -H 'Content-Type: application/json' \
  -d "{\"email\":\"$EMAIL\",\"password\":\"$PASS\"}" | jq -r .accessToken)
AUTH=(-H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json')

say "Register service 1 of 2  ->  PostgreSQL"
curl -fsS -X POST "$API/system/services" "${AUTH[@]}" -d '{
  "name":"sales-pg","label":"Sales (PostgreSQL)","connectorType":"postgresql",
  "connection":{"host":"sampledb","port":5432,"database":"northwind",
                "username":"demo","password":"demo-pw","tls":{"mode":"disable"}}}' \
  | jq -c '{name,connectorType,status}' 2>/dev/null || echo '  (already registered)'

say "Register service 2 of 2  ->  MySQL  (different engine, same platform)"
curl -fsS -X POST "$API/system/services" "${AUTH[@]}" -d '{
  "name":"sales-my","label":"Sales (MySQL)","connectorType":"mysql",
  "connection":{"host":"sampledb-mysql","port":3306,"database":"northwind",
                "username":"demo","password":"demo-pw","tls":{"mode":"disable"}}}' \
  | jq -c '{name,connectorType,status}' 2>/dev/null || echo '  (already registered)'

say "Refresh schema on both (introspection) — wait for Active"
for svc in sales-pg sales-my; do
  ID=$(curl -fsS "$API/system/services" "${AUTH[@]}" | jq -r --arg n "$svc" '.[]|select(.name==$n)|.id')
  curl -fsS -X POST "$API/system/services/$ID/refresh" "${AUTH[@]}" >/dev/null 2>&1 || true
  ST=""
  for i in $(seq 1 40); do
    ST=$(curl -fsS "$API/system/services/$ID" "${AUTH[@]}" | jq -r .status)
    case "$ST" in Active|Failed) break;; esac
    sleep 2
  done
  printf '  %-9s id=%-3s status=%s\n' "$svc" "$ID" "$ST"
done

say "THE POINT: byte-for-byte identical OData query against both engines"
for svc in sales-pg sales-my; do
  printf '\n--- %s ---\n' "$svc"
  curl -fsS -G "$API/api/odata/$svc/customers" "${AUTH[@]}" \
    --data-urlencode '$filter=country eq '"'"'US'"'"'' \
    --data-urlencode '$select=id,name,country' \
    --data-urlencode '$orderby=id' \
    --data-urlencode '$top=3' | jq -c '.value'
done

say "Same data over REST, and each service publishes OpenAPI 3.1"
for svc in sales-pg sales-my; do
  printf '  %-9s rest: %s | openapi paths: %s\n' "$svc" \
    "$(curl -fsS -G "$API/api/rest/$svc/_table/customers" "${AUTH[@]}" --data-urlencode 'limit=1' 2>/dev/null | jq -c '.resource[0].name' 2>/dev/null || echo n/a)" \
    "$(curl -fsS "$API/api/odata/$svc/openapi.json" "${AUTH[@]}" 2>/dev/null | jq '.paths|length' 2>/dev/null || echo n/a)"
done

say "Beat 3: interrogate the data — discovery endpoints"
SVC=sales-pg
printf '  tables      : %s\n' "$(curl -fsS "$API/api/odata/$SVC/" "${AUTH[@]}" | jq -c '[.value[].name]')"
printf '  $metadata   : %s bytes of CSDL\n' "$(curl -fsS "$API/api/odata/$SVC/\$metadata" "${AUTH[@]}" | wc -c | tr -d ' ')"
printf '  customers   : %s\n' "$(curl -fsS "$API/api/rest/$SVC/_table/customers/_schema" "${AUTH[@]}" | jq -c '[.fields[]|"\(.name):\(.type)"]')"
printf '  PII flagged : %s\n' "$(curl -fsS "$API/api/rest/$SVC/_table/customers/_schema" "${AUTH[@]}" | jq -c '[.fields[]|select(.description!=null)|{name,description}]')"

say "Beat 3b: the same data over MCP (JSON-RPC, for LLM clients)"
ROLE=$(curl -fsS "$API/system/roles" "${AUTH[@]}" | jq -r '.[0].id')
curl -fsS -X POST "$API/system/apps" "${AUTH[@]}" \
  -d "{\"name\":\"mcp-explorer\",\"description\":\"demo\",\"roleId\":$ROLE,\"isActive\":true,\"mcpEnabled\":true}" \
  >/dev/null 2>&1 || true
APPID=$(curl -fsS "$API/system/apps" "${AUTH[@]}" | jq -r '.[]|select(.name=="mcp-explorer")|.id')
# The full key is returned exactly once, so mint a fresh one each run.
KEY=$(curl -fsS -X POST "$API/system/apps/$APPID/keys" "${AUTH[@]}" -d '{"name":"demo"}' | jq -r .key)
MCP=(-H "X-API-Key: $KEY" -H 'Content-Type: application/json')
printf '  tools for key : %s\n' "$(curl -fsS "$API/mcp/health" -H "X-API-Key: $KEY" | jq -c .toolsAvailableForKey)"
printf '  tools         : %s\n' "$(curl -fsS -X POST "$API/mcp" "${MCP[@]}" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' | jq -c '[.result.tools[].name]')"
printf '  sales-my_query: %s\n' "$(curl -fsS -X POST "$API/mcp" "${MCP[@]}" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"sales-my_query","arguments":{"table":"customers","filter":"country='"'"'US'"'"'","fields":["id","name"],"limit":3}}}' \
  | jq -c '.result.content[0].text|fromjson|.rows')"

say "Beat 3c: browsable OpenAPI (Swagger UI on :8081)"
mkdir -p "$(dirname "$0")/specs"
for svc in sales-pg sales-my; do
  for kind in odata rest; do
    curl -fsS "$API/api/$kind/$svc/openapi.json" "${AUTH[@]}" > "$(dirname "$0")/specs/$kind-$svc.json"
  done
done
printf '  exported %s specs -> ./specs/\n' "$(ls "$(dirname "$0")"/specs/*.json | wc -l | tr -d ' ')"
printf '  open: http://localhost:8081  ("Try it out" works — CORS allows :8081)\n'
