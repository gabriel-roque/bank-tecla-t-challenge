using Ledger.Domain;

namespace Ledger.Application;

public interface ILedgerSession
{
    Task<IdempotencyEntry?> FindIdempotencyAsync(
        string clientId,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<AggregateState> LoadAggregateAsync(
        Guid accountId,
        DateTimeOffset? at,
        CancellationToken cancellationToken);

    Task AppendEventAsync(LedgerEvent ledgerEvent, CancellationToken cancellationToken);

    Task SaveSnapshotAsync(AccountSnapshot snapshot, CancellationToken cancellationToken);

    Task SaveIdempotencyAsync(IdempotencyEntry entry, CancellationToken cancellationToken);

    Task SaveOutboxAsync(OutboxMessage message, CancellationToken cancellationToken);
}
