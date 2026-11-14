namespace Ledger.Application;

public interface IOutboxPublisher
{
    Task<int> PublishPendingAsync(CancellationToken cancellationToken);
}
