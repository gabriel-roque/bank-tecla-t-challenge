#!/bin/sh
set -eu

docker compose stop ledger-worker-1 ledger-worker-2
printf '%s\n' 'Workers parados. Movimentos continuam no Ledger e acumulam no Outbox.'
printf '%s\n' 'Para recuperar: docker compose start ledger-worker-1 ledger-worker-2'
