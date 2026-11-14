#!/bin/sh
set -eu

psql --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
    --set=replication_password="$LEDGER_REPLICATION_PASSWORD" \
    --set=app_password="$LEDGER_APP_PASSWORD" \
    --set=worker_password="$LEDGER_WORKER_PASSWORD" \
    --set=query_password="$LEDGER_QUERY_PASSWORD" <<'SQL'
SELECT format('CREATE ROLE replicator WITH REPLICATION LOGIN PASSWORD %L', :'replication_password')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'replicator') \gexec
SELECT format('CREATE ROLE ledger_app LOGIN PASSWORD %L', :'app_password')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ledger_app') \gexec
SELECT format('CREATE ROLE ledger_worker LOGIN PASSWORD %L', :'worker_password')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ledger_worker') \gexec
SELECT format('CREATE ROLE ledger_query LOGIN PASSWORD %L', :'query_password')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ledger_query') \gexec
GRANT CONNECT ON DATABASE ledger TO ledger_app, ledger_worker, ledger_query;
SQL

printf '\nhost replication replicator 0.0.0.0/0 scram-sha-256\n' >> "$PGDATA/pg_hba.conf"
