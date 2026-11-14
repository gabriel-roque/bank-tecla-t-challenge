using System.Text.Json;
using Ledger.Application;
using Npgsql;

namespace Ledger.Adapters.Persistence.ReadModel;

public sealed class PostgresReadModelRebuilder(
    NpgsqlDataSource ledgerDataSource,
    NpgsqlDataSource readDataSource,
    IReadModelStore readModelStore)
{
    public async Task<long> RebuildAsync(CancellationToken cancellationToken)
    {
        await using (NpgsqlConnection readConnection = await readDataSource.OpenConnectionAsync(cancellationToken))
        await using (NpgsqlCommand truncate = new(
                         "TRUNCATE TABLE processed_events, statement_entries, account_balances;",
                         readConnection))
        {
            await truncate.ExecuteNonQueryAsync(cancellationToken);
        }

        const string sql = """
            SELECT event_id, account_id, sequence, event_type, event_version, amount_minor, currency,
                   effective_at, recorded_at, transaction_id, correlation_id, payload
            FROM ledger_events
            ORDER BY account_id, sequence;
            """;
        long count = 0;
        await using NpgsqlConnection ledgerConnection = await ledgerDataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlCommand command = new(sql, ledgerConnection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            using JsonDocument payload = JsonDocument.Parse(reader.GetString(11));
            string description = payload.RootElement.TryGetProperty("description", out JsonElement descriptionElement)
                ? descriptionElement.GetString() ?? string.Empty
                : string.Empty;
            string? externalReference = payload.RootElement.TryGetProperty("externalReference", out JsonElement externalElement)
                ? externalElement.GetString()
                : null;
            LedgerEventEnvelope envelope = new(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetInt16(4),
                reader.GetInt64(5),
                reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7),
                reader.GetFieldValue<DateTimeOffset>(8),
                reader.GetGuid(9),
                reader.GetGuid(10),
                description,
                externalReference);
            await readModelStore.ApplyAsync(envelope, cancellationToken);
            count++;
        }

        await reader.CloseAsync();

        await using NpgsqlConnection validationConnection = await readDataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlCommand validationCommand = new(
            "SELECT COUNT(*) FROM processed_events WHERE projection_name = 'account-balance-v1';",
            validationConnection);
        long projected = (long)(await validationCommand.ExecuteScalarAsync(cancellationToken)
                                ?? throw new InvalidOperationException("Não foi possível validar o replay."));
        if (projected != count)
        {
            throw new InvalidOperationException(
                $"Replay inconsistente: {count} eventos lidos e {projected} eventos projetados.");
        }

        return count;
    }
}
