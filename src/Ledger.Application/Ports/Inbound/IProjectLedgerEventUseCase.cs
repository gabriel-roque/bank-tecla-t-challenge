namespace Ledger.Application;

public interface IProjectLedgerEventUseCase
{
    Task ProjectAsync(LedgerEventEnvelope envelope, CancellationToken cancellationToken);
}
