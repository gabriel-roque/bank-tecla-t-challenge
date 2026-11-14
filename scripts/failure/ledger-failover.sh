#!/bin/sh
set -eu

docker compose stop ledger-db-primary
docker compose exec -T ledger-db-standby pg_ctl promote -D /var/lib/postgresql/data
docker compose exec -T ledger-db-standby psql -U ledger_admin -d ledger -c "ALTER SYSTEM SET synchronous_standby_names = ''"
docker compose exec -T ledger-db-standby psql -U ledger_admin -d ledger -c "SELECT pg_reload_conf()"
printf '%s\n' 'Standby promovido. As aplicações usam multi-host e reconectam ao writer disponível.'
printf '%s\n' 'Este cenário é controlado e destrutivo para a topologia local; recrie os volumes para restaurar a réplica.'
