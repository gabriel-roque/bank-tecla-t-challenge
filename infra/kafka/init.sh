#!/bin/sh
set -eu

BOOTSTRAP_SERVERS=${BOOTSTRAP_SERVERS:-kafka-1:9092,kafka-2:9092,kafka-3:9092}
TOPIC=${TOPIC:-ledger.movements.v1}
PARTITIONS=${PARTITIONS:-6}

topics() {
  /opt/kafka/bin/kafka-topics.sh --bootstrap-server "$BOOTSTRAP_SERVERS" "$@"
}

if ! topics --list | grep -Fxq "$TOPIC"; then
  topics --create \
    --topic "$TOPIC" \
    --partitions "$PARTITIONS" \
    --replication-factor 3 \
    --config min.insync.replicas=2
else
  current_partitions=$(topics --describe --topic "$TOPIC" | sed -n 's/.*PartitionCount: \([0-9][0-9]*\).*/\1/p' | head -n 1)
  if [ -z "$current_partitions" ]; then
    echo "Não foi possível determinar a quantidade de partições de $TOPIC." >&2
    exit 1
  fi
  if [ "$current_partitions" -lt "$PARTITIONS" ]; then
    topics --alter --topic "$TOPIC" --partitions "$PARTITIONS"
  elif [ "$current_partitions" -gt "$PARTITIONS" ]; then
    echo "$TOPIC já possui $current_partitions partições; redução para $PARTITIONS não é suportada." >&2
    exit 1
  fi

  /opt/kafka/bin/kafka-configs.sh \
    --bootstrap-server "$BOOTSTRAP_SERVERS" \
    --alter \
    --entity-type topics \
    --entity-name "$TOPIC" \
    --add-config min.insync.replicas=2

  current_description=$(topics --describe --topic "$TOPIC")
  if ! printf '%s\n' "$current_description" | awk '
    /^[[:space:]]*Topic: .*Partition:/ {
      replicas = $0
      sub(/^.*Replicas: /, "", replicas)
      sub(/[[:space:]]+Isr:.*$/, "", replicas)
      count = split(replicas, values, ",")
      delete seen
      for (i = 1; i <= count; i++) seen[values[i]] = 1
      if (count != 3 || !seen[1] || !seen[2] || !seen[3]) exit 1
      partitions++
    }
    END { if (partitions != 6) exit 1 }
  '; then
    /opt/kafka/bin/kafka-reassign-partitions.sh \
      --bootstrap-server "$BOOTSTRAP_SERVERS" \
      --reassignment-json-file /kafka-init/ledger-movements-reassignment.json \
      --execute
  fi
fi

attempt=0
while [ "$attempt" -lt 60 ]; do
  description=$(topics --describe --topic "$TOPIC")
  if printf '%s\n' "$description" | awk '
    /PartitionCount:/ && $0 !~ /ReplicationFactor: 3/ { exit 1 }
    /^[[:space:]]*Topic: .*Partition:/ {
      replicas = $0
      sub(/^.*Replicas: /, "", replicas)
      sub(/[[:space:]]+Isr:.*$/, "", replicas)
      if (split(replicas, values, ",") != 3) exit 1
      isr = $0
      sub(/^.*Isr: /, "", isr)
      sub(/[[:space:]]+Elr:.*$/, "", isr)
      if (split(isr, values, ",") != 3) exit 1
      partitions++
    }
    END { if (partitions != 6) exit 1 }
  '; then
    printf '%s\n' "$description"
    exit 0
  fi
  attempt=$((attempt + 1))
  sleep 1
done

echo "O tópico $TOPIC não convergiu para 6 partições com fator de replicação 3." >&2
exit 1
