#!/usr/bin/env sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
REPOSITORY_ROOT=$(CDPATH= cd -- "$SCRIPT_DIR/../.." && pwd)
GATLING_DIRECTORY="$REPOSITORY_ROOT/tests-performance/gatling"

command -v node >/dev/null 2>&1 || {
  echo "Node.js 22+ não foi encontrado no PATH." >&2
  exit 1
}
command -v npm >/dev/null 2>&1 || {
  echo "npm não foi encontrado no PATH." >&2
  exit 1
}

BASE_URL=${BASE_URL:-http://localhost:8080}
ACCOUNT_ID=${ACCOUNT_ID:-11111111-1111-1111-1111-111111111111}
ACCOUNT_POOL_SIZE=${ACCOUNT_POOL_SIZE:-500}
LEDGER_RPS=${LEDGER_RPS:-500}
QUERY_RPS=${QUERY_RPS:-1000}
RAMP_SECONDS=${RAMP_SECONDS:-5}
STEADY_SECONDS=${STEADY_SECONDS:-10}

if [ -z "${ACCESS_TOKEN:-}" ]; then
  if [ "$ACCOUNT_POOL_SIZE" -gt 1 ]; then
    ACCESS_TOKEN=$(node "$REPOSITORY_ROOT/scripts/dev/generate-token.mjs")
  else
    ACCESS_TOKEN=$(node "$REPOSITORY_ROOT/scripts/dev/generate-token.mjs" "$ACCOUNT_ID")
  fi
fi

export BASE_URL ACCOUNT_ID ACCOUNT_POOL_SIZE ACCESS_TOKEN LEDGER_RPS QUERY_RPS RAMP_SECONDS STEADY_SECONDS

node -e '
const url = process.argv[1];
fetch(url, { signal: AbortSignal.timeout(5000) })
  .then((response) => {
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
  })
  .catch((error) => {
    console.error(`API indisponível em ${url}: ${error.message}`);
    process.exit(1);
  });
' "$BASE_URL/health/live"

cd "$GATLING_DIRECTORY"
if [ ! -x node_modules/.bin/gatling ]; then
  npm ci
fi

npm run typecheck

echo "Executando todos os cenários Gatling em $BASE_URL"
echo "ledger=$LEDGER_RPS req/s query=$QUERY_RPS req/s accounts=$ACCOUNT_POOL_SIZE ramp=${RAMP_SECONDS}s steady=${STEADY_SECONDS}s"

found=false
for simulation_file in ./*.gatling.ts; do
  if [ ! -f "$simulation_file" ]; then
    continue
  fi

  found=true
  simulation=$(basename "$simulation_file" .gatling.ts)
  echo "Executando simulação Gatling: $simulation"
  ./node_modules/.bin/gatling run \
    --sources-folder . \
    --typescript \
    --simulation "$simulation"
done

if [ "$found" = false ]; then
  echo "Nenhuma simulação *.gatling.ts foi encontrada em $GATLING_DIRECTORY." >&2
  exit 1
fi
