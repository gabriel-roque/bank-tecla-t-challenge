namespace Ledger.Application;

public interface IReadModelStore
{
    Task<BalanceResult?> GetBalanceAsync(Guid accountId, CancellationToken cancellationToken);

    Task<MovementDetails?> GetMovementAsync(Guid movementId, CancellationToken cancellationToken);

    Task<StatementPage> GetStatementAsync(StatementQuery query, CancellationToken cancellationToken);

    Task<ProjectionApplyResult> ApplyAsync(
        LedgerEventEnvelope envelope,
        CancellationToken cancellationToken);
}

public enum ProjectionApplyResult
{
    Applied,
    Duplicate
}
