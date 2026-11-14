#!/bin/sh
set -eu

if [ ! -s "$PGDATA/PG_VERSION" ]; then
    rm -rf "${PGDATA:?}"/*
    until PGPASSWORD="$LEDGER_REPLICATION_PASSWORD" pg_basebackup \
        --host=ledger-db-primary \
        --username=replicator \
        --pgdata="$PGDATA" \
        --wal-method=stream \
        --write-recovery-conf \
        --progress
    do
        sleep 2
    done
    chmod 0700 "$PGDATA"
fi

exec docker-entrypoint.sh postgres -c hot_standby=on -c max_connections=150
