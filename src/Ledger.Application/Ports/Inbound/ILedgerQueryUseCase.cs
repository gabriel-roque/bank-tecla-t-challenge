namespace Ledger.Application;

public interface ILedgerQueryUseCase
{
    Task<BalanceResult> GetBalanceAsync(
        Guid accountId,
        bool strong,
        DateTimeOffset? at,
        CancellationToken cancellationToken);

    Task<MovementDetails?> GetMovementAsync(Guid movementId, CancellationToken cancellationToken);

    Task<StatementPage> GetStatementAsync(StatementQuery query, CancellationToken cancellationToken);
}
