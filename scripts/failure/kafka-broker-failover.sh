#!/bin/sh
set -eu

BROKER=${BROKER:-kafka-3}
TOPIC=${TOPIC:-ledger.movements.v1}
GROUP_ID=${GROUP_ID:-account-balance-projection-v1}
BOOTSTRAP_SERVERS=kafka-1:9092,kafka-2:9092,kafka-3:9092
MAX_WAIT_SECONDS=${MAX_WAIT_SECONDS:-120}

case "$BROKER" in
  kafka-1|kafka-2|kafka-3) ;;
  *)
    echo "BROKER deve ser kafka-1, kafka-2 ou kafka-3." >&2
    exit 1
    ;;
esac

command -v node >/dev/null 2>&1 || {
  echo "Node.js 22+ não foi encontrado no PATH." >&2
  exit 1
}

kafka_exec() {
  service=kafka-1
  if [ "$BROKER" = kafka-1 ]; then
    service=kafka-2
  fi
  docker compose exec -T "$service" "$@"
}

topic_description() {
  kafka_exec /opt/kafka/bin/kafka-topics.sh \
    --bootstrap-server "$BOOTSTRAP_SERVERS" \
    --describe \
    --topic "$TOPIC"
}

assert_topic_shape() {
  description=$1
  printf '%s\n' "$description" | awk '
    /PartitionCount:/ {
      if ($0 !~ /PartitionCount: 6/ || $0 !~ /ReplicationFactor: 3/) exit 1
      header++
    }
    /^[[:space:]]*Topic: .*Partition:/ {
      replicas = $0
      sub(/^.*Replicas: /, "", replicas)
      sub(/[[:space:]]+Isr:.*$/, "", replicas)
      if (split(replicas, values, ",") != 3) exit 1
      partitions++
    }
    END { if (header != 1 || partitions != 6) exit 1 }
  '
}

assert_min_isr() {
  kafka_exec /opt/kafka/bin/kafka-configs.sh \
    --bootstrap-server "$BOOTSTRAP_SERVERS" \
    --describe \
    --entity-type topics \
    --entity-name "$TOPIC" | grep -q 'min.insync.replicas=2'
}

isr_size_is_at_least() {
  minimum=$1
  topic_description | awk -v minimum="$minimum" '
    /^[[:space:]]*Topic: .*Partition:/ {
      isr = $0
      sub(/^.*Isr: /, "", isr)
      sub(/[[:space:]]+Elr:.*$/, "", isr)
      if (split(isr, values, ",") < minimum) exit 1
      partitions++
    }
    END { if (partitions != 6) exit 1 }
  '
}

wait_until() {
  description=$1
  shift
  attempt=0
  while [ "$attempt" -lt "$MAX_WAIT_SECONDS" ]; do
    if "$@"; then
      return 0
    fi
    attempt=$((attempt + 1))
    sleep 1
  done
  echo "Timeout aguardando: $description" >&2
  return 1
}

pending_outbox_is_zero() {
  count=$(docker compose exec -T ledger-db-primary \
    psql -U ledger_admin -d ledger -tAc \
    "SELECT COUNT(*) FROM outbox_messages WHERE published_at IS NULL;")
  [ "$count" -eq 0 ]
}

consumer_lag_is_zero() {
  output=$(kafka_exec /opt/kafka/bin/kafka-consumer-groups.sh \
    --bootstrap-server "$BOOTSTRAP_SERVERS" \
    --group "$GROUP_ID" \
    --describe 2>/dev/null) || return 1
  printf '%s\n' "$output" | awk '
    $1 == "GROUP" { next }
    $6 ~ /^[0-9]+$/ { rows++; if ($6 != 0) exit 1 }
    END { if (rows == 0) exit 1 }
  '
}

restore_broker() {
  docker compose start "$BROKER" >/dev/null
}

baseline=$(topic_description)
assert_topic_shape "$baseline"
assert_min_isr
wait_until 'todas as partições com três réplicas sincronizadas antes da falha' isr_size_is_at_least 3

trap restore_broker EXIT INT TERM
docker compose stop "$BROKER"
wait_until 'quórum de duas réplicas sincronizadas após a queda' isr_size_is_at_least 2

failure_account=$(node -e "console.log(require('node:crypto').randomUUID())")
ACCOUNT_ID="$failure_account" node scripts/validation/smoke.mjs
wait_until 'Outbox drenado durante a falha de um broker' pending_outbox_is_zero
wait_until 'consumer lag zerado durante a falha de um broker' consumer_lag_is_zero

restore_broker
wait_until 'broker recuperado e todas as partições novamente com três ISR' isr_size_is_at_least 3

recovery_account=$(node -e "console.log(require('node:crypto').randomUUID())")
ACCOUNT_ID="$recovery_account" node scripts/validation/smoke.mjs
wait_until 'Outbox drenado depois da recuperação' pending_outbox_is_zero
wait_until 'offsets alcançarem o fim depois da recuperação' consumer_lag_is_zero

trap - EXIT INT TERM
printf '%s\n' "Failover Kafka OK: $BROKER saiu, o cluster continuou projetando e retornou com ISR=3 e lag=0."
