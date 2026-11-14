using System.Data;
using System.Text.Json;
using Ledger.Application;
using Ledger.Domain;
using Npgsql;

namespace Ledger.Adapters.Persistence.Ledger;

public sealed class PostgresLedgerStore(NpgsqlDataSource dataSource) : ILedgerStore
{
    private const string UniqueViolation = "23505";

    public async Task<T> ExecuteAsync<T>(
        Func<ILedgerSession, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        PostgresLedgerSession session = new(connection, transaction);

        try
        {
            T result = await operation(session, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (PostgresException exception) when (exception.SqlState == UniqueViolation)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new ConcurrencyConflictException("Conflito de versão ou idempotência no Ledger.", exception);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<AggregateState> LoadAggregateAsync(
        Guid accountId,
        DateTimeOffset? at,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await LoadAggregateInternalAsync(connection, null, accountId, at, cancellationToken);
    }

    public async Task<IReadOnlyList<LedgerEvent>> ReadEventsAsync(
        Guid accountId,
        long fromSequence,
        long toSequence,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT event_id, account_id, sequence, event_type, event_version, amount_minor, currency,
                   effective_at, recorded_at, transaction_id, causation_id, correlation_id, payload
            FROM ledger_events
            WHERE account_id = @account_id
              AND sequence BETWEEN @from_sequence AND @to_sequence
            ORDER BY sequence;
            """;

        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("from_sequence", fromSequence);
        command.Parameters.AddWithValue("to_sequence", toSequence);
        return await ReadEventsAsync(command, cancellationToken);
    }

    private static async Task<AggregateState> LoadAggregateInternalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid accountId,
        DateTimeOffset? at,
        CancellationToken cancellationToken)
    {
        string snapshotSql = at is null
            ? """
              SELECT account_id, sequence, balance_minor, currency, schema_version, checksum, created_at, last_recorded_at
              FROM account_snapshots
              WHERE account_id = @account_id
              ORDER BY sequence DESC
              LIMIT 1;
              """
            : """
              SELECT account_id, sequence, balance_minor, currency, schema_version, checksum, created_at, last_recorded_at
              FROM account_snapshots
              WHERE account_id = @account_id AND last_recorded_at <= @at
              ORDER BY sequence DESC
              LIMIT 1;
              """;

        AccountSnapshot? snapshot;
        await using (NpgsqlCommand command = new(snapshotSql, connection, transaction))
        {
            command.Parameters.AddWithValue("account_id", accountId);
            if (at is not null)
            {
                command.Parameters.AddWithValue("at", at.Value);
            }

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            snapshot = await reader.ReadAsync(cancellationToken)
                ? new AccountSnapshot(
                    reader.GetGuid(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetInt16(4),
                    reader.GetString(5),
                    reader.GetFieldValue<DateTimeOffset>(6),
                    reader.GetFieldValue<DateTimeOffset>(7))
                : null;
        }

        string eventsSql = at is null
            ? """
              SELECT event_id, account_id, sequence, event_type, event_version, amount_minor, currency,
                     effective_at, recorded_at, transaction_id, causation_id, correlation_id, payload
              FROM ledger_events
              WHERE account_id = @account_id AND sequence > @after_sequence
              ORDER BY sequence;
              """
            : """
              SELECT event_id, account_id, sequence, event_type, event_version, amount_minor, currency,
                     effective_at, recorded_at, transaction_id, causation_id, correlation_id, payload
              FROM ledger_events
              WHERE account_id = @account_id
                AND sequence > @after_sequence
                AND recorded_at <= @at
              ORDER BY sequence;
              """;

        await using NpgsqlCommand eventsCommand = new(eventsSql, connection, transaction);
        eventsCommand.Parameters.AddWithValue("account_id", accountId);
        eventsCommand.Parameters.AddWithValue("after_sequence", snapshot?.Sequence ?? 0L);
        if (at is not null)
        {
            eventsCommand.Parameters.AddWithValue("at", at.Value);
        }

        IReadOnlyList<LedgerEvent> events = await ReadEventsAsync(eventsCommand, cancellationToken);
        return new AggregateState(snapshot, events);
    }

    private static async Task<IReadOnlyList<LedgerEvent>> ReadEventsAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        List<LedgerEvent> events = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            string eventType = reader.GetString(3);
            MovementType movementType = eventType switch
            {
                "CreditPosted" => MovementType.Credit,
                "DebitPosted" => MovementType.Debit,
                _ => throw new InvalidLedgerEventException($"Tipo de evento persistido não suportado: {eventType}.")
            };
            string payload = reader.GetString(12);
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            string description = root.TryGetProperty("description", out JsonElement descriptionElement)
                ? descriptionElement.GetString() ?? string.Empty
                : string.Empty;
            string? externalReference = root.TryGetProperty("externalReference", out JsonElement externalElement)
                ? externalElement.GetString()
                : null;

            events.Add(new LedgerEvent(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetInt64(2),
                movementType,
                reader.GetInt16(4),
                reader.GetInt64(5),
                reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7),
                reader.GetFieldValue<DateTimeOffset>(8),
                reader.GetGuid(9),
                reader.IsDBNull(10) ? null : reader.GetGuid(10),
                reader.GetGuid(11),
                description,
                externalReference));
        }

        return events;
    }

    private sealed class PostgresLedgerSession(NpgsqlConnection connection, NpgsqlTransaction transaction) : ILedgerSession
    {
        public async Task<IdempotencyEntry?> FindIdempotencyAsync(
            string clientId,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            const string sql = """
                SELECT client_id, idempotency_key, request_hash, event_id, response_status,
                       response_body::text, created_at
                FROM idempotency_requests
                WHERE client_id = @client_id AND idempotency_key = @idempotency_key;
                """;

            await using NpgsqlCommand command = new(sql, connection, transaction);
            command.Parameters.AddWithValue("client_id", clientId);
            command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

            return await reader.ReadAsync(cancellationToken)
                ? new IdempotencyEntry(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetGuid(3),
                    reader.GetInt16(4),
                    reader.GetString(5),
                    reader.GetFieldValue<DateTimeOffset>(6))
                : null;
        }

        public Task<AggregateState> LoadAggregateAsync(
            Guid accountId,
            DateTimeOffset? at,
            CancellationToken cancellationToken) =>
            LoadAggregateInternalAsync(connection, transaction, accountId, at, cancellationToken);

        public async Task AppendEventAsync(LedgerEvent ledgerEvent, CancellationToken cancellationToken)
        {
            const string sql = """
                INSERT INTO ledger_events (
                    event_id, account_id, sequence, event_type, event_version, amount_minor, currency,
                    effective_at, recorded_at, transaction_id, causation_id, correlation_id, payload, metadata)
                VALUES (
                    @event_id, @account_id, @sequence, @event_type, @event_version, @amount_minor, @currency,
                    @effective_at, @recorded_at, @transaction_id, @causation_id, @correlation_id,
                    @payload::jsonb, @metadata::jsonb);
                """;
            string payload = JsonSerializer.Serialize(
                new { ledgerEvent.Description, ledgerEvent.ExternalReference },
                JsonSerializerOptions.Web);
            string metadata = JsonSerializer.Serialize(
                new { ledgerEvent.CorrelationId },
                JsonSerializerOptions.Web);

            await using NpgsqlCommand command = new(sql, connection, transaction);
            command.Parameters.AddWithValue("event_id", ledgerEvent.EventId);
            command.Parameters.AddWithValue("account_id", ledgerEvent.AccountId);
            command.Parameters.AddWithValue("sequence", ledgerEvent.Sequence);
            command.Parameters.AddWithValue("event_type", ledgerEvent.EventType);
            command.Parameters.AddWithValue("event_version", ledgerEvent.EventVersion);
            command.Parameters.AddWithValue("amount_minor", ledgerEvent.AmountMinor);
            command.Parameters.AddWithValue("currency", ledgerEvent.Currency);
            command.Parameters.AddWithValue("effective_at", ledgerEvent.EffectiveAt);
            command.Parameters.AddWithValue("recorded_at", ledgerEvent.RecordedAt);
            command.Parameters.AddWithValue("transaction_id", ledgerEvent.TransactionId);
            command.Parameters.AddWithValue("causation_id", (object?)ledgerEvent.CausationId ?? DBNull.Value);
            command.Parameters.AddWithValue("correlation_id", ledgerEvent.CorrelationId);
            command.Parameters.AddWithValue("payload", payload);
            command.Parameters.AddWithValue("metadata", metadata);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task SaveSnapshotAsync(AccountSnapshot snapshot, CancellationToken cancellationToken)
        {
            const string sql = """
                INSERT INTO account_snapshots (
                    account_id, sequence, balance_minor, currency, schema_version,
                    checksum, created_at, last_recorded_at)
                VALUES (
                    @account_id, @sequence, @balance_minor, @currency, @schema_version,
                    @checksum, @created_at, @last_recorded_at)
                ON CONFLICT (account_id, sequence) DO NOTHING;
                """;

            await using NpgsqlCommand command = new(sql, connection, transaction);
            command.Parameters.AddWithValue("account_id", snapshot.AccountId);
            command.Parameters.AddWithValue("sequence", snapshot.Sequence);
            command.Parameters.AddWithValue("balance_minor", snapshot.BalanceMinor);
            command.Parameters.AddWithValue("currency", snapshot.Currency);
            command.Parameters.AddWithValue("schema_version", snapshot.SchemaVersion);
            command.Parameters.AddWithValue("checksum", snapshot.Checksum);
            command.Parameters.AddWithValue("created_at", snapshot.CreatedAt);
            command.Parameters.AddWithValue("last_recorded_at", snapshot.LastRecordedAt);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task SaveIdempotencyAsync(IdempotencyEntry entry, CancellationToken cancellationToken)
        {
            const string sql = """
                INSERT INTO idempotency_requests (
                    client_id, idempotency_key, request_hash, event_id,
                    response_status, response_body, created_at)
                VALUES (
                    @client_id, @idempotency_key, @request_hash, @event_id,
                    @response_status, @response_body::jsonb, @created_at);
                """;

            await using NpgsqlCommand command = new(sql, connection, transaction);
            command.Parameters.AddWithValue("client_id", entry.ClientId);
            command.Parameters.AddWithValue("idempotency_key", entry.IdempotencyKey);
            command.Parameters.AddWithValue("request_hash", entry.RequestHash);
            command.Parameters.AddWithValue("event_id", entry.EventId);
            command.Parameters.AddWithValue("response_status", entry.ResponseStatus);
            command.Parameters.AddWithValue("response_body", entry.ResponseBody);
            command.Parameters.AddWithValue("created_at", entry.CreatedAt);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task SaveOutboxAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            const string sql = """
                INSERT INTO outbox_messages (
                    message_id, topic, partition_key, aggregate_sequence,
                    payload, headers, created_at)
                VALUES (
                    @message_id, @topic, @partition_key, @aggregate_sequence,
                    @payload::jsonb, @headers::jsonb, @created_at);
                """;

            await using NpgsqlCommand command = new(sql, connection, transaction);
            command.Parameters.AddWithValue("message_id", message.MessageId);
            command.Parameters.AddWithValue("topic", message.Topic);
            command.Parameters.AddWithValue("partition_key", message.PartitionKey);
            command.Parameters.AddWithValue("aggregate_sequence", message.AggregateSequence);
            command.Parameters.AddWithValue("payload", message.Payload);
            command.Parameters.AddWithValue("headers", message.Headers);
            command.Parameters.AddWithValue("created_at", message.CreatedAt);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
