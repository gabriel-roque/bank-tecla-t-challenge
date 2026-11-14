#!/bin/sh
set -eu

docker compose stop kafka-1 kafka-2
printf '%s\n' 'Kafka parado. Registre movimentos e verifique outbox_messages com published_at NULL.'
printf '%s\n' 'Para recuperar: docker compose start kafka-1 kafka-2 && docker compose start kafka-init'
