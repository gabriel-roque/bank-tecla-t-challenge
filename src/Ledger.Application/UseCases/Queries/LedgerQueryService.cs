using Ledger.Domain;

namespace Ledger.Application;

public sealed class LedgerQueryService(
    IReadModelStore readModel,
    ILedgerStore ledgerStore) : ILedgerQueryUseCase
{
    public async Task<BalanceResult> GetBalanceAsync(
        Guid accountId,
        bool strong,
        DateTimeOffset? at,
        CancellationToken cancellationToken)
    {
        if (!strong && at is null)
        {
            BalanceResult? projected = await readModel.GetBalanceAsync(accountId, cancellationToken);
            return projected ?? new BalanceResult(accountId, 0, AccountAggregate.SupportedCurrency, 0, null, "eventual");
        }

        AggregateState state = await ledgerStore.LoadAggregateAsync(accountId, at, cancellationToken);
        AccountAggregate aggregate = AccountAggregate.Rehydrate(accountId, state.Snapshot, state.Events);
        return new BalanceResult(
            accountId,
            aggregate.BalanceMinor,
            AccountAggregate.SupportedCurrency,
            aggregate.Sequence,
            aggregate.LastRecordedAt,
            "strong");
    }

    public Task<MovementDetails?> GetMovementAsync(Guid movementId, CancellationToken cancellationToken) =>
        readModel.GetMovementAsync(movementId, cancellationToken);

    public Task<StatementPage> GetStatementAsync(StatementQuery query, CancellationToken cancellationToken) =>
        readModel.GetStatementAsync(query, cancellationToken);
}
