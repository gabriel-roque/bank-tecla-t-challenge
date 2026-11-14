using Npgsql;

namespace Ledger.Adapters.Persistence.Migrations;

public static class DatabaseMigrator
{
    private const long LedgerMigrationLock = 7_341_001;
    private const long ReadMigrationLock = 7_341_002;

    public static async Task MigrateLedgerAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await AcquireLockAsync(connection, LedgerMigrationLock, cancellationToken);

        try
        {
            const string sql = """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version VARCHAR(40) PRIMARY KEY,
                    applied_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp()
                );

                CREATE TABLE IF NOT EXISTS ledger_events (
                    event_id UUID PRIMARY KEY,
                    account_id UUID NOT NULL,
                    sequence BIGINT NOT NULL,
                    event_type VARCHAR(80) NOT NULL CHECK (event_type IN ('CreditPosted', 'DebitPosted')),
                    event_version SMALLINT NOT NULL CHECK (event_version > 0),
                    amount_minor BIGINT NOT NULL CHECK (amount_minor > 0),
                    currency CHAR(3) NOT NULL CHECK (currency = 'BRL'),
                    effective_at TIMESTAMPTZ NOT NULL,
                    recorded_at TIMESTAMPTZ NOT NULL,
                    transaction_id UUID NOT NULL,
                    causation_id UUID NULL,
                    correlation_id UUID NOT NULL,
                    payload JSONB NOT NULL,
                    metadata JSONB NOT NULL,
                    CONSTRAINT uq_ledger_events_account_sequence UNIQUE (account_id, sequence)
                );

                CREATE INDEX IF NOT EXISTS ix_ledger_events_account_recorded_sequence
                    ON ledger_events (account_id, recorded_at, sequence);

                CREATE TABLE IF NOT EXISTS account_snapshots (
                    account_id UUID NOT NULL,
                    sequence BIGINT NOT NULL,
                    balance_minor BIGINT NOT NULL,
                    currency CHAR(3) NOT NULL CHECK (currency = 'BRL'),
                    schema_version SMALLINT NOT NULL,
                    checksum VARCHAR(128) NOT NULL,
                    created_at TIMESTAMPTZ NOT NULL,
                    last_recorded_at TIMESTAMPTZ NOT NULL,
                    PRIMARY KEY (account_id, sequence)
                );

                CREATE INDEX IF NOT EXISTS ix_account_snapshots_historical
                    ON account_snapshots (account_id, last_recorded_at DESC, sequence DESC);

                CREATE TABLE IF NOT EXISTS idempotency_requests (
                    client_id VARCHAR(100) NOT NULL,
                    idempotency_key VARCHAR(100) NOT NULL,
                    request_hash VARCHAR(128) NOT NULL,
                    event_id UUID NOT NULL,
                    response_status SMALLINT NOT NULL,
                    response_body JSONB NOT NULL,
                    created_at TIMESTAMPTZ NOT NULL,
                    PRIMARY KEY (client_id, idempotency_key)
                );

                CREATE TABLE IF NOT EXISTS outbox_messages (
                    message_id UUID PRIMARY KEY,
                    topic VARCHAR(150) NOT NULL,
                    partition_key UUID NOT NULL,
                    aggregate_sequence BIGINT NOT NULL,
                    payload JSONB NOT NULL,
                    headers JSONB NOT NULL,
                    created_at TIMESTAMPTZ NOT NULL,
                    published_at TIMESTAMPTZ NULL,
                    attempts INTEGER NOT NULL DEFAULT 0 CHECK (attempts >= 0),
                    last_error TEXT NULL,
                    CONSTRAINT uq_outbox_partition_sequence UNIQUE (partition_key, aggregate_sequence)
                );

                CREATE INDEX IF NOT EXISTS ix_outbox_pending_partition_sequence
                    ON outbox_messages (partition_key, aggregate_sequence)
                    WHERE published_at IS NULL;

                CREATE INDEX IF NOT EXISTS ix_outbox_pending_created_message
                    ON outbox_messages (created_at, message_id)
                    WHERE published_at IS NULL;

                DO $grants$
                BEGIN
                    IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'ledger_app') THEN
                        GRANT USAGE ON SCHEMA public TO ledger_app;
                        GRANT SELECT, INSERT ON ledger_events, account_snapshots, idempotency_requests, outbox_messages TO ledger_app;
                    END IF;
                    IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'ledger_worker') THEN
                        GRANT USAGE ON SCHEMA public TO ledger_worker;
                        GRANT SELECT ON ledger_events, account_snapshots TO ledger_worker;
                        GRANT SELECT, UPDATE ON outbox_messages TO ledger_worker;
                    END IF;
                    IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'ledger_query') THEN
                        GRANT USAGE ON SCHEMA public TO ledger_query;
                        GRANT SELECT ON ledger_events, account_snapshots TO ledger_query;
                    END IF;
                END
                $grants$;

                INSERT INTO schema_migrations (version)
                VALUES ('ledger-001-initial')
                ON CONFLICT (version) DO NOTHING;
                """;

            await using NpgsqlCommand command = new(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await ReleaseLockAsync(connection, LedgerMigrationLock, cancellationToken);
        }
    }

    public static async Task MigrateReadAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await AcquireLockAsync(connection, ReadMigrationLock, cancellationToken);

        try
        {
            const string sql = """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version VARCHAR(40) PRIMARY KEY,
                    applied_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp()
                );

                CREATE TABLE IF NOT EXISTS account_balances (
                    account_id UUID PRIMARY KEY,
                    balance_minor BIGINT NOT NULL,
                    currency CHAR(3) NOT NULL CHECK (currency = 'BRL'),
                    last_sequence BIGINT NOT NULL CHECK (last_sequence >= 0),
                    last_event_id UUID NOT NULL,
                    updated_at TIMESTAMPTZ NOT NULL
                );

                CREATE TABLE IF NOT EXISTS statement_entries (
                    event_id UUID PRIMARY KEY,
                    account_id UUID NOT NULL,
                    sequence BIGINT NOT NULL,
                    movement_type VARCHAR(20) NOT NULL CHECK (movement_type IN ('CREDIT', 'DEBIT')),
                    amount_minor BIGINT NOT NULL CHECK (amount_minor > 0),
                    balance_after_minor BIGINT NOT NULL,
                    currency CHAR(3) NOT NULL CHECK (currency = 'BRL'),
                    description VARCHAR(255) NOT NULL,
                    effective_at TIMESTAMPTZ NOT NULL,
                    recorded_at TIMESTAMPTZ NOT NULL,
                    transaction_id UUID NOT NULL,
                    CONSTRAINT uq_statement_account_sequence UNIQUE (account_id, sequence)
                );

                CREATE INDEX IF NOT EXISTS ix_statement_account_recorded
                    ON statement_entries (account_id, recorded_at DESC, sequence DESC, event_id DESC);

                CREATE INDEX IF NOT EXISTS ix_statement_account_effective
                    ON statement_entries (account_id, effective_at DESC);

                CREATE TABLE IF NOT EXISTS processed_events (
                    projection_name VARCHAR(100) NOT NULL,
                    event_id UUID NOT NULL,
                    account_id UUID NOT NULL,
                    sequence BIGINT NOT NULL,
                    processed_at TIMESTAMPTZ NOT NULL,
                    PRIMARY KEY (projection_name, event_id)
                );

                DO $grants$
                BEGIN
                    IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'read_projector') THEN
                        GRANT USAGE ON SCHEMA public TO read_projector;
                        GRANT SELECT, INSERT, UPDATE ON account_balances, statement_entries, processed_events TO read_projector;
                    END IF;
                    IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'read_query') THEN
                        GRANT USAGE ON SCHEMA public TO read_query;
                        GRANT SELECT ON account_balances, statement_entries, processed_events TO read_query;
                    END IF;
                END
                $grants$;

                INSERT INTO schema_migrations (version)
                VALUES ('read-001-initial')
                ON CONFLICT (version) DO NOTHING;
                """;

            await using NpgsqlCommand command = new(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await ReleaseLockAsync(connection, ReadMigrationLock, cancellationToken);
        }
    }

    private static async Task AcquireLockAsync(NpgsqlConnection connection, long lockId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new("SELECT pg_advisory_lock(@lock_id);", connection);
        command.Parameters.AddWithValue("lock_id", lockId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReleaseLockAsync(NpgsqlConnection connection, long lockId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new("SELECT pg_advisory_unlock(@lock_id);", connection);
        command.Parameters.AddWithValue("lock_id", lockId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
