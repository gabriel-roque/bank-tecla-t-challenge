using Ledger.Domain;

namespace Ledger.Application;

public interface ILedgerStore
{
    Task<T> ExecuteAsync<T>(
        Func<ILedgerSession, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);

    Task<AggregateState> LoadAggregateAsync(
        Guid accountId,
        DateTimeOffset? at,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<LedgerEvent>> ReadEventsAsync(
        Guid accountId,
        long fromSequence,
        long toSequence,
        CancellationToken cancellationToken);
}
