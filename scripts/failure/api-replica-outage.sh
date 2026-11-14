#!/bin/sh
set -eu

restore_replicas() {
  docker compose start ledger-api-1 query-api-1
  docker compose restart edge
}

trap restore_replicas EXIT
docker compose stop ledger-api-1 query-api-1
node scripts/validation/smoke.mjs
trap - EXIT
restore_replicas
printf '%s\n' 'Uma réplica de cada API foi interrompida; o fluxo continuou pelas réplicas restantes.'
