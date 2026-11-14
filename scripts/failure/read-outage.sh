#!/bin/sh
set -eu

docker compose stop read-db
printf '%s\n' 'Read Model parado. Saldo deve usar fallback forte; extrato e movimentação devem retornar 503.'
printf '%s\n' 'Para recuperar: docker compose start read-db ledger-worker-1 ledger-worker-2'
