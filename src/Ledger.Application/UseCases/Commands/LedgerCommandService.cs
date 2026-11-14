using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ledger.Domain;

namespace Ledger.Application;

public sealed class LedgerCommandService(
    ILedgerStore ledgerStore,
    TimeProvider timeProvider) : IPostMovementUseCase
{
    public const string TopicName = "ledger.movements.v1";
    private const int MaximumAttempts = 3;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<MovementResult> PostAsync(PostMovementCommand command, CancellationToken cancellationToken)
    {
        string requestHash = ComputeRequestHash(command);

        for (int attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                return await ledgerStore.ExecuteAsync(
                    (session, token) => ExecuteOnceAsync(session, command, requestHash, token),
                    cancellationToken);
            }
            catch (ConcurrencyConflictException) when (attempt < MaximumAttempts)
            {
                int jitterMilliseconds = Random.Shared.Next(0, 11);
                await Task.Delay(
                    TimeSpan.FromMilliseconds((attempt * 10) + jitterMilliseconds),
                    timeProvider,
                    cancellationToken);
            }
        }

        throw new ConcurrencyConflictException("Não foi possível confirmar a movimentação após novas tentativas.");
    }

    private async Task<MovementResult> ExecuteOnceAsync(
        ILedgerSession session,
        PostMovementCommand command,
        string requestHash,
        CancellationToken cancellationToken)
    {
        IdempotencyEntry? existing = await session.FindIdempotencyAsync(
            command.ClientId,
            command.IdempotencyKey,
            cancellationToken);

        if (existing is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(existing.RequestHash),
                    Convert.FromHexString(requestHash)))
            {
                throw new IdempotencyConflictException();
            }

            return JsonSerializer.Deserialize<MovementResult>(existing.ResponseBody, JsonOptions)
                   ?? throw new InvalidOperationException("Resposta de idempotência inválida.");
        }

        AggregateState state = await session.LoadAggregateAsync(command.AccountId, null, cancellationToken);
        AccountAggregate account = AccountAggregate.Rehydrate(command.AccountId, state.Snapshot, state.Events);
        DateTimeOffset now = timeProvider.GetUtcNow();
        Guid eventId = Guid.NewGuid();
        Guid transactionId = Guid.NewGuid();
        LedgerEvent ledgerEvent = account.Post(
            command.Type,
            command.Amount,
            command.Currency,
            command.EffectiveAt,
            now,
            eventId,
            transactionId,
            command.CorrelationId,
            command.Description,
            command.ExternalReference);

        MovementResult result = new(
            ledgerEvent.EventId,
            ledgerEvent.AccountId,
            ledgerEvent.Sequence,
            ledgerEvent.MovementType,
            ledgerEvent.AmountMinor,
            ledgerEvent.Currency,
            account.BalanceMinor,
            ledgerEvent.EffectiveAt,
            ledgerEvent.RecordedAt,
            ledgerEvent.CorrelationId);

        LedgerEventEnvelope envelope = ToEnvelope(ledgerEvent);
        string responseBody = JsonSerializer.Serialize(result, JsonOptions);
        string payload = JsonSerializer.Serialize(envelope, JsonOptions);
        string headers = JsonSerializer.Serialize(
            new { eventVersion = ledgerEvent.EventVersion, correlationId = ledgerEvent.CorrelationId },
            JsonOptions);

        await session.AppendEventAsync(ledgerEvent, cancellationToken);
        await session.SaveOutboxAsync(
            new OutboxMessage(eventId, TopicName, command.AccountId, ledgerEvent.Sequence, payload, headers, now),
            cancellationToken);
        await session.SaveIdempotencyAsync(
            new IdempotencyEntry(
                command.ClientId,
                command.IdempotencyKey,
                requestHash,
                eventId,
                201,
                responseBody,
                now),
            cancellationToken);

        if (ledgerEvent.Sequence % 50 == 0)
        {
            await session.SaveSnapshotAsync(account.CreateSnapshot(now), cancellationToken);
        }

        return result;
    }

    public static LedgerEventEnvelope ToEnvelope(LedgerEvent ledgerEvent) => new(
        ledgerEvent.EventId,
        ledgerEvent.AccountId,
        ledgerEvent.Sequence,
        ledgerEvent.EventType,
        ledgerEvent.EventVersion,
        ledgerEvent.AmountMinor,
        ledgerEvent.Currency,
        ledgerEvent.EffectiveAt,
        ledgerEvent.RecordedAt,
        ledgerEvent.TransactionId,
        ledgerEvent.CorrelationId,
        ledgerEvent.Description,
        ledgerEvent.ExternalReference);

    private static string ComputeRequestHash(PostMovementCommand command)
    {
        string canonical = string.Join(
            '|',
            command.AccountId.ToString("N", CultureInfo.InvariantCulture),
            command.Type.ToString(),
            command.Amount.ToString(CultureInfo.InvariantCulture),
            command.Currency,
            command.EffectiveAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            command.Description,
            command.ExternalReference ?? string.Empty);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
