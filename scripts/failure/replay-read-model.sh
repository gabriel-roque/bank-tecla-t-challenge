#!/bin/sh
set -eu

docker compose stop ledger-worker-1 ledger-worker-2 query-api-1 query-api-2
docker compose run --rm \
  -e ReplayOnly=true \
  -e "ConnectionStrings__Read=Host=read-db;Port=5432;Database=ledger_read;Username=read_admin;Password=${READ_ADMIN_PASSWORD:-read_admin_dev}" \
  ledger-worker-1
docker compose start query-api-1 query-api-2 ledger-worker-1 ledger-worker-2
# As APIs podem ser recriadas com novos IPs durante testes; o restart força
# uma resolução imediata dos upstreams sem depender do TTL do DNS interno.
docker compose restart edge
printf '%s\n' 'Read Model reconstruído a partir do Ledger.'
