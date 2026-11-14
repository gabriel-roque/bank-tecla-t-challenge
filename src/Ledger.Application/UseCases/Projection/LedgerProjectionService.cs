using Ledger.Domain;

namespace Ledger.Application;

public sealed class LedgerProjectionService(
    IReadModelStore readModel,
    ILedgerStore ledgerStore) : IProjectLedgerEventUseCase
{
    public async Task ProjectAsync(
        LedgerEventEnvelope envelope,
        CancellationToken cancellationToken)
    {
        try
        {
            await readModel.ApplyAsync(envelope, cancellationToken);
        }
        catch (ProjectionSequenceGapException gap)
        {
            IReadOnlyList<LedgerEvent> missing = await ledgerStore.ReadEventsAsync(
                gap.AccountId,
                gap.Expected,
                gap.Actual - 1,
                cancellationToken);
            if (missing.Count != gap.Actual - gap.Expected)
            {
                throw new UnrecoverableProjectionGapException(
                    gap.AccountId,
                    gap.Expected,
                    gap.Actual,
                    gap);
            }

            foreach (LedgerEvent missingEvent in missing.OrderBy(static item => item.Sequence))
            {
                await readModel.ApplyAsync(LedgerCommandService.ToEnvelope(missingEvent), cancellationToken);
            }

            await readModel.ApplyAsync(envelope, cancellationToken);
        }
    }
}
