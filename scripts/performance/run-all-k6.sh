#!/usr/bin/env sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
REPOSITORY_ROOT=$(CDPATH= cd -- "$SCRIPT_DIR/../.." && pwd)
K6_DIRECTORY="$REPOSITORY_ROOT/tests-performance/k6"
K6_BIN=${K6_BIN:-k6}
K6_DOCKER_IMAGE=${K6_DOCKER_IMAGE:-grafana/k6:latest}
K6_REPORTS_DIRECTORY=${K6_REPORTS_DIRECTORY:-$REPOSITORY_ROOT/reports/k6}
K6_RUN_ID=${K6_RUN_ID:-$(date -u +%Y%m%dT%H%M%SZ)}
K6_EXPORT_RAW=${K6_EXPORT_RAW:-false}
K6_DASHBOARD_PERIOD=${K6_WEB_DASHBOARD_PERIOD:-1s}
RUN_REPORTS_DIRECTORY="$K6_REPORTS_DIRECTORY/$K6_RUN_ID"
DASHBOARDS_DIRECTORY="$RUN_REPORTS_DIRECTORY/dashboards"
SUMMARIES_DIRECTORY="$RUN_REPORTS_DIRECTORY/summaries"
RAW_DIRECTORY="$RUN_REPORTS_DIRECTORY/raw"

command -v node >/dev/null 2>&1 || {
  echo "Node.js 22+ não foi encontrado no PATH." >&2
  exit 1
}
BASE_URL=${BASE_URL:-http://localhost:8080}
ACCOUNT_ID=${ACCOUNT_ID:-11111111-1111-1111-1111-111111111111}
ACCOUNT_POOL_SIZE=${ACCOUNT_POOL_SIZE:-500}
LEDGER_RPS=${LEDGER_RPS:-500}
QUERY_RPS=${QUERY_RPS:-1000}
DURATION=${DURATION:-15s}
STRESS_STAGE_DURATION=${STRESS_STAGE_DURATION:-5s}
SPIKE_DURATION=${SPIKE_DURATION:-5s}
SOAK_DURATION=${SOAK_DURATION:-$DURATION}
BREAKPOINT_STAGE_DURATION=${BREAKPOINT_STAGE_DURATION:-5s}
HTTP_TIMEOUT=${HTTP_TIMEOUT:-15s}
K6_COOLDOWN_SECONDS=${K6_COOLDOWN_SECONDS:-15}
K6_ACCOUNT_POOL_BASE=${K6_ACCOUNT_POOL_BASE:-$((($(date +%s) % 1000000) * 100000))}

if [ -z "${ACCESS_TOKEN:-}" ]; then
  if [ "$ACCOUNT_POOL_SIZE" -gt 1 ]; then
    ACCESS_TOKEN=$(node "$REPOSITORY_ROOT/scripts/dev/generate-token.mjs")
  else
    ACCESS_TOKEN=$(node "$REPOSITORY_ROOT/scripts/dev/generate-token.mjs" "$ACCOUNT_ID")
  fi
fi

export BASE_URL ACCOUNT_ID ACCOUNT_POOL_SIZE ACCESS_TOKEN LEDGER_RPS QUERY_RPS DURATION
export STRESS_STAGE_DURATION SPIKE_DURATION SOAK_DURATION BREAKPOINT_STAGE_DURATION
export HTTP_TIMEOUT

mkdir -p "$DASHBOARDS_DIRECTORY" "$SUMMARIES_DIRECTORY"
if [ "$K6_EXPORT_RAW" = true ]; then
  mkdir -p "$RAW_DIRECTORY"
fi

print_report_location() {
  echo "Relatórios k6: $RUN_REPORTS_DIRECTORY"
}

trap print_report_location 0

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

if command -v "$K6_BIN" >/dev/null 2>&1; then
  K6_MODE=native
elif command -v docker >/dev/null 2>&1; then
  K6_MODE=docker
else
  echo "Nem k6 nem Docker foram encontrados. Instale k6 ou informe K6_BIN=/caminho/para/k6." >&2
  exit 1
fi

echo "Executando todos os cenários k6 em $BASE_URL"
echo "ledger=$LEDGER_RPS req/s query=$QUERY_RPS req/s accounts=$ACCOUNT_POOL_SIZE duration=$DURATION"
echo "dashboards=$DASHBOARDS_DIRECTORY"

found=false
failed_scenarios=""
scenario_index=0
for scenario_file in "$K6_DIRECTORY"/*.js; do
  if [ ! -f "$scenario_file" ]; then
    continue
  fi

  found=true
  scenario_filename=$(basename "$scenario_file")
  scenario_name=${scenario_filename%.js}
  dashboard_file="$DASHBOARDS_DIRECTORY/$scenario_name.html"
  summary_file="$SUMMARIES_DIRECTORY/$scenario_name.json"
  account_pool_offset=$((K6_ACCOUNT_POOL_BASE + (scenario_index * ACCOUNT_POOL_SIZE)))

  if [ "$scenario_index" -gt 0 ] && [ "$K6_COOLDOWN_SECONDS" -gt 0 ]; then
    echo "Aguardando ${K6_COOLDOWN_SECONDS}s para isolar o próximo perfil..."
    sleep "$K6_COOLDOWN_SECONDS"
  fi

  echo "Executando arquivo k6: $scenario_filename (account offset=$account_pool_offset)"
  if [ "$K6_MODE" = native ]; then
    set -- run --summary-export "$summary_file"
    if [ "$K6_EXPORT_RAW" = true ]; then
      set -- "$@" --out "json=$RAW_DIRECTORY/$scenario_name.json"
    fi
    set -- "$@" "$scenario_file"

    if ! ACCOUNT_POOL_OFFSET="$account_pool_offset" \
      K6_PROFILE="$scenario_name" \
      K6_WEB_DASHBOARD=true \
      K6_WEB_DASHBOARD_PORT=-1 \
      K6_WEB_DASHBOARD_OPEN=false \
      K6_WEB_DASHBOARD_PERIOD="$K6_DASHBOARD_PERIOD" \
      K6_WEB_DASHBOARD_EXPORT="$dashboard_file" \
      "$K6_BIN" "$@"; then
      failed_scenarios="$failed_scenarios $scenario_name"
    fi
  else
    docker_base_url=$BASE_URL
    if [ "$docker_base_url" = "http://localhost:8080" ] || [ "$docker_base_url" = "http://127.0.0.1:8080" ]; then
      docker_base_url=http://host.docker.internal:8080
    fi

    set -- run --summary-export "/reports/summaries/$scenario_name.json"
    if [ "$K6_EXPORT_RAW" = true ]; then
      set -- "$@" --out "json=/reports/raw/$scenario_name.json"
    fi
    set -- "$@" "/tests/$scenario_filename"

    if ! docker run --rm \
      --user "$(id -u):$(id -g)" \
      --add-host host.docker.internal:host-gateway \
      -e BASE_URL="$docker_base_url" \
      -e ACCOUNT_ID="$ACCOUNT_ID" \
      -e ACCOUNT_POOL_SIZE="$ACCOUNT_POOL_SIZE" \
      -e ACCOUNT_POOL_OFFSET="$account_pool_offset" \
      -e K6_PROFILE="$scenario_name" \
      -e ACCESS_TOKEN="$ACCESS_TOKEN" \
      -e LEDGER_RPS="$LEDGER_RPS" \
      -e QUERY_RPS="$QUERY_RPS" \
      -e DURATION="$DURATION" \
      -e STRESS_STAGE_DURATION="$STRESS_STAGE_DURATION" \
      -e SPIKE_DURATION="$SPIKE_DURATION" \
      -e SOAK_DURATION="$SOAK_DURATION" \
      -e BREAKPOINT_STAGE_DURATION="$BREAKPOINT_STAGE_DURATION" \
      -e HTTP_TIMEOUT="$HTTP_TIMEOUT" \
      -e K6_WEB_DASHBOARD=true \
      -e K6_WEB_DASHBOARD_PORT=-1 \
      -e K6_WEB_DASHBOARD_OPEN=false \
      -e K6_WEB_DASHBOARD_PERIOD="$K6_DASHBOARD_PERIOD" \
      -e K6_WEB_DASHBOARD_EXPORT="/reports/dashboards/$scenario_name.html" \
      -v "$K6_DIRECTORY:/tests:ro" \
      -v "$RUN_REPORTS_DIRECTORY:/reports" \
      "$K6_DOCKER_IMAGE" "$@"; then
      failed_scenarios="$failed_scenarios $scenario_name"
    fi
  fi
  scenario_index=$((scenario_index + 1))
done

if [ "$found" = false ]; then
  echo "Nenhum cenário *.js foi encontrado em $K6_DIRECTORY." >&2
  exit 1
fi

if [ -n "$failed_scenarios" ]; then
  echo "Cenários com falha:$failed_scenarios" >&2
  exit 1
fi
