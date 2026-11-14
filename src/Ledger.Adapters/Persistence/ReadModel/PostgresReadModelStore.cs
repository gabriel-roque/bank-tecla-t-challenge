using System.Globalization;
using System.Text;
using Ledger.Application;
using Ledger.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Ledger.Adapters.Persistence.ReadModel;

public sealed class PostgresReadModelStore(NpgsqlDataSource dataSource, TimeProvider timeProvider) : IReadModelStore
{
    private const string ProjectionName = "account-balance-v1";

    public async Task<BalanceResult?> GetBalanceAsync(Guid accountId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT account_id, balance_minor, currency, last_sequence, updated_at
            FROM account_balances
            WHERE account_id = @account_id;
            """;

        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("account_id", accountId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken)
            ? new BalanceResult(
                reader.GetGuid(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                "eventual")
            : null;
    }

    public async Task<MovementDetails?> GetMovementAsync(Guid movementId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT event_id, account_id, sequence, movement_type, amount_minor,
                   balance_after_minor, currency, description, effective_at, recorded_at, transaction_id
            FROM statement_entries
            WHERE event_id = @event_id;
            """;

        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("event_id", movementId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMovement(reader) : null;
    }

    public async Task<StatementPage> GetStatementAsync(StatementQuery query, CancellationToken cancellationToken)
    {
        StatementCursor? cursor = DecodeCursor(query.Cursor);
        int limit = Math.Clamp(query.Limit, 1, 200);
        const string sql = """
            SELECT event_id, account_id, sequence, movement_type, amount_minor,
                   balance_after_minor, currency, description, effective_at, recorded_at, transaction_id
            FROM statement_entries
            WHERE account_id = @account_id
              AND (@from_at IS NULL OR recorded_at >= @from_at)
              AND (@to_at IS NULL OR recorded_at <= @to_at)
              AND (
                    @cursor_at IS NULL
                    OR (recorded_at, sequence, event_id) < (@cursor_at, @cursor_sequence, @cursor_event_id)
                  )
            ORDER BY recorded_at DESC, sequence DESC, event_id DESC
            LIMIT @take;
            """;

        List<MovementDetails> items = [];
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (NpgsqlCommand command = new(sql, connection))
        {
            command.Parameters.AddWithValue("account_id", query.AccountId);
            command.Parameters.Add("from_at", NpgsqlDbType.TimestampTz).Value = (object?)query.From ?? DBNull.Value;
            command.Parameters.Add("to_at", NpgsqlDbType.TimestampTz).Value = (object?)query.To ?? DBNull.Value;
            command.Parameters.Add("cursor_at", NpgsqlDbType.TimestampTz).Value = (object?)cursor?.RecordedAt ?? DBNull.Value;
            command.Parameters.Add("cursor_sequence", NpgsqlDbType.Bigint).Value = cursor?.Sequence ?? 0L;
            command.Parameters.Add("cursor_event_id", NpgsqlDbType.Uuid).Value = cursor?.EventId ?? Guid.Empty;
            command.Parameters.AddWithValue("take", limit + 1);

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(ReadMovement(reader));
            }
        }

        string? nextCursor = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            MovementDetails last = items[^1];
            nextCursor = EncodeCursor(new StatementCursor(last.RecordedAt, last.Sequence, last.MovementId));
        }

        long checkpoint = 0;
        await using (NpgsqlCommand checkpointCommand = new(
                         "SELECT COALESCE(last_sequence, 0) FROM account_balances WHERE account_id = @account_id;",
                         connection))
        {
            checkpointCommand.Parameters.AddWithValue("account_id", query.AccountId);
            object? value = await checkpointCommand.ExecuteScalarAsync(cancellationToken);
            checkpoint = value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        return new StatementPage(items, nextCursor, checkpoint);
    }

    public async Task<ProjectionApplyResult> ApplyAsync(
        LedgerEventEnvelope envelope,
        CancellationToken cancellationToken)
    {
        envelope.Validate();
        MovementType movementType = envelope.GetMovementType();

        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            const string inboxSql = """
                INSERT INTO processed_events (projection_name, event_id, account_id, sequence, processed_at)
                VALUES (@projection_name, @event_id, @account_id, @sequence, @processed_at)
                ON CONFLICT (projection_name, event_id) DO NOTHING
                RETURNING event_id;
                """;
            await using NpgsqlCommand inboxCommand = new(inboxSql, connection, transaction);
            inboxCommand.Parameters.AddWithValue("projection_name", ProjectionName);
            inboxCommand.Parameters.AddWithValue("event_id", envelope.EventId);
            inboxCommand.Parameters.AddWithValue("account_id", envelope.AccountId);
            inboxCommand.Parameters.AddWithValue("sequence", envelope.Sequence);
            inboxCommand.Parameters.AddWithValue("processed_at", timeProvider.GetUtcNow());
            object? inserted = await inboxCommand.ExecuteScalarAsync(cancellationToken);

            if (inserted is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return ProjectionApplyResult.Duplicate;
            }

            const string balanceSql = """
                SELECT balance_minor, last_sequence
                FROM account_balances
                WHERE account_id = @account_id
                FOR UPDATE;
                """;
            long currentBalance = 0;
            long currentSequence = 0;
            await using (NpgsqlCommand balanceCommand = new(balanceSql, connection, transaction))
            {
                balanceCommand.Parameters.AddWithValue("account_id", envelope.AccountId);
                await using NpgsqlDataReader reader = await balanceCommand.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    currentBalance = reader.GetInt64(0);
                    currentSequence = reader.GetInt64(1);
                }
            }

            long expected = currentSequence + 1;
            if (envelope.Sequence != expected)
            {
                throw new ProjectionSequenceGapException(envelope.AccountId, expected, envelope.Sequence);
            }

            long signedAmount = movementType == MovementType.Credit ? envelope.Amount : -envelope.Amount;
            long nextBalance = checked(currentBalance + signedAmount);
            if (nextBalance < 0)
            {
                throw new InvalidOperationException("A projeção produziria saldo negativo para um evento já confirmado.");
            }

            const string upsertSql = """
                INSERT INTO account_balances (
                    account_id, balance_minor, currency, last_sequence, last_event_id, updated_at)
                VALUES (
                    @account_id, @balance_minor, @currency, @last_sequence, @last_event_id, @updated_at)
                ON CONFLICT (account_id) DO UPDATE SET
                    balance_minor = EXCLUDED.balance_minor,
                    currency = EXCLUDED.currency,
                    last_sequence = EXCLUDED.last_sequence,
                    last_event_id = EXCLUDED.last_event_id,
                    updated_at = EXCLUDED.updated_at;
                """;
            await using (NpgsqlCommand upsertCommand = new(upsertSql, connection, transaction))
            {
                upsertCommand.Parameters.AddWithValue("account_id", envelope.AccountId);
                upsertCommand.Parameters.AddWithValue("balance_minor", nextBalance);
                upsertCommand.Parameters.AddWithValue("currency", envelope.Currency);
                upsertCommand.Parameters.AddWithValue("last_sequence", envelope.Sequence);
                upsertCommand.Parameters.AddWithValue("last_event_id", envelope.EventId);
                upsertCommand.Parameters.AddWithValue("updated_at", envelope.RecordedAt);
                await upsertCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            const string statementSql = """
                INSERT INTO statement_entries (
                    event_id, account_id, sequence, movement_type, amount_minor, balance_after_minor,
                    currency, description, effective_at, recorded_at, transaction_id)
                VALUES (
                    @event_id, @account_id, @sequence, @movement_type, @amount_minor, @balance_after_minor,
                    @currency, @description, @effective_at, @recorded_at, @transaction_id);
                """;
            await using (NpgsqlCommand statementCommand = new(statementSql, connection, transaction))
            {
                statementCommand.Parameters.AddWithValue("event_id", envelope.EventId);
                statementCommand.Parameters.AddWithValue("account_id", envelope.AccountId);
                statementCommand.Parameters.AddWithValue("sequence", envelope.Sequence);
                statementCommand.Parameters.AddWithValue(
                    "movement_type",
                    movementType == MovementType.Credit ? "CREDIT" : "DEBIT");
                statementCommand.Parameters.AddWithValue("amount_minor", envelope.Amount);
                statementCommand.Parameters.AddWithValue("balance_after_minor", nextBalance);
                statementCommand.Parameters.AddWithValue("currency", envelope.Currency);
                statementCommand.Parameters.AddWithValue("description", envelope.Description);
                statementCommand.Parameters.AddWithValue("effective_at", envelope.EffectiveAt);
                statementCommand.Parameters.AddWithValue("recorded_at", envelope.RecordedAt);
                statementCommand.Parameters.AddWithValue("transaction_id", envelope.TransactionId);
                await statementCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return ProjectionApplyResult.Applied;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static MovementDetails ReadMovement(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetInt64(2),
        ParseMovementType(reader.GetString(3)),
        reader.GetInt64(4),
        reader.GetInt64(5),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetFieldValue<DateTimeOffset>(8),
        reader.GetFieldValue<DateTimeOffset>(9),
        reader.GetGuid(10));

    private static MovementType ParseMovementType(string movementType) => movementType switch
    {
        "CREDIT" => MovementType.Credit,
        "DEBIT" => MovementType.Debit,
        _ => throw new InvalidLedgerEventException($"Tipo de movimentação projetada não suportado: {movementType}.")
    };

    private static string EncodeCursor(StatementCursor cursor)
    {
        string raw = string.Create(
            CultureInfo.InvariantCulture,
            $"{cursor.RecordedAt:O}|{cursor.Sequence}|{cursor.EventId:N}");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static StatementCursor? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        try
        {
            string normalized = cursor.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + ((4 - (normalized.Length % 4)) % 4), '=');
            string[] parts = Encoding.UTF8.GetString(Convert.FromBase64String(normalized)).Split('|');
            if (parts.Length != 3)
            {
                throw new FormatException();
            }

            return new StatementCursor(
                DateTimeOffset.Parse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                long.Parse(parts[1], CultureInfo.InvariantCulture),
                Guid.ParseExact(parts[2], "N"));
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            throw new ArgumentException("Cursor inválido.", nameof(cursor), exception);
        }
    }

    private sealed record StatementCursor(DateTimeOffset RecordedAt, long Sequence, Guid EventId);
}
