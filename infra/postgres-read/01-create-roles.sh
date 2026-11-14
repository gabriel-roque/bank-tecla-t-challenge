#!/bin/sh
set -eu

psql --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
    --set=projector_password="$READ_PROJECTOR_PASSWORD" \
    --set=query_password="$READ_QUERY_PASSWORD" <<'SQL'
SELECT format('CREATE ROLE read_projector LOGIN PASSWORD %L', :'projector_password')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'read_projector') \gexec
SELECT format('CREATE ROLE read_query LOGIN PASSWORD %L', :'query_password')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'read_query') \gexec
GRANT CONNECT ON DATABASE ledger_read TO read_projector, read_query;
SQL
