using Confluent.Kafka;
using Ledger.Application;
using Npgsql;

namespace Ledger.Adapters.Messaging.Kafka;

public sealed class PostgresOutboxPublisher(
    NpgsqlDataSource ledgerDataSource,
    IProducer<string, string> producer,
    int batchSize = 32) : IOutboxPublisher
{
    private readonly int batchSize = Math.Clamp(batchSize, 1, 100);

    public async Task<int> PublishPendingAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await ledgerDataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted,
            cancellationToken);
        List<OutboxClaim> claims = [];

        try
        {
            const string selectSql = """
                SELECT message_id, topic, partition_key, aggregate_sequence, payload::text, headers::text
                FROM outbox_messages AS candidate
                WHERE candidate.published_at IS NULL
                  AND NOT EXISTS (
                      SELECT 1
                      FROM outbox_messages AS previous
                      WHERE previous.partition_key = candidate.partition_key
                        AND previous.published_at IS NULL
                        AND previous.aggregate_sequence < candidate.aggregate_sequence
                  )
                ORDER BY candidate.created_at, candidate.message_id
                FOR UPDATE OF candidate SKIP LOCKED
                LIMIT @batch_size;
                """;

            await using (NpgsqlCommand selectCommand = new(selectSql, connection, transaction))
            {
                selectCommand.Parameters.AddWithValue("batch_size", batchSize);
                await using NpgsqlDataReader reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    claims.Add(new OutboxClaim(
                        reader.GetGuid(0),
                        reader.GetString(1),
                        reader.GetGuid(2),
                        reader.GetInt64(3),
                        reader.GetString(4),
                        reader.GetString(5)));
                }
            }

            foreach (OutboxClaim claim in claims)
            {
                Message<string, string> message = new()
                {
                    Key = claim.PartitionKey.ToString("N"),
                    Value = claim.Payload,
                    Headers =
                    [
                        new Header("event_id", System.Text.Encoding.UTF8.GetBytes(claim.MessageId.ToString("N"))),
                        new Header("sequence", System.Text.Encoding.UTF8.GetBytes(claim.AggregateSequence.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                    ]
                };
                await producer.ProduceAsync(claim.Topic, message, cancellationToken);
            }

            if (claims.Count > 0)
            {
                const string updateSql = """
                    UPDATE outbox_messages
                    SET published_at = clock_timestamp(), attempts = attempts + 1, last_error = NULL
                    WHERE message_id = ANY(@published_ids);
                    """;
                await using NpgsqlCommand updateCommand = new(updateSql, connection, transaction);
                updateCommand.Parameters.AddWithValue("published_ids", claims.Select(static claim => claim.MessageId).ToArray());
                await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return claims.Count;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            if (claims.Count > 0)
            {
                await RecordFailureAsync(claims, exception.Message, CancellationToken.None);
            }

            throw;
        }
    }

    private async Task RecordFailureAsync(
        IReadOnlyCollection<OutboxClaim> claims,
        string error,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE outbox_messages
            SET attempts = attempts + 1, last_error = left(@error, 2000)
            WHERE message_id = ANY(@message_ids) AND published_at IS NULL;
            """;
        await using NpgsqlConnection connection = await ledgerDataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("error", error);
        command.Parameters.AddWithValue("message_ids", claims.Select(static claim => claim.MessageId).ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record OutboxClaim(
        Guid MessageId,
        string Topic,
        Guid PartitionKey,
        long AggregateSequence,
        string Payload,
        string Headers);
}
