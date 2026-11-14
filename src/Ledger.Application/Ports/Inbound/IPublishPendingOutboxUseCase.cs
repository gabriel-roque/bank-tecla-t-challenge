namespace Ledger.Application;

public interface IPublishPendingOutboxUseCase
{
    Task<int> PublishPendingAsync(CancellationToken cancellationToken);
}
