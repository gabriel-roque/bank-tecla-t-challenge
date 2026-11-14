namespace Ledger.Application;

public sealed class OutboxPublishingService(IOutboxPublisher publisher)
    : IPublishPendingOutboxUseCase
{
    public Task<int> PublishPendingAsync(CancellationToken cancellationToken) =>
        publisher.PublishPendingAsync(cancellationToken);
}
