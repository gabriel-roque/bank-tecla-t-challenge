using Ledger.Application;
using Ledger.Worker.State;

namespace Ledger.Worker.Services;

internal sealed class OutboxPublisherBackgroundService(
    IPublishPendingOutboxUseCase publishPendingOutbox,
    WorkerState state,
    TimeProvider timeProvider,
    ILogger<OutboxPublisherBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int published = await publishPendingOutbox.PublishPendingAsync(stoppingToken);
                state.PublisherLastSuccessAt = timeProvider.GetUtcNow();
                failures = 0;
                if (published == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), timeProvider, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                failures++;
                logger.LogWarning(exception, "Falha recuperável ao publicar o Outbox.");
                double backoffMilliseconds = Math.Min(5_000, 100 * Math.Pow(2, Math.Min(failures, 5)));
                TimeSpan delay = TimeSpan.FromMilliseconds(backoffMilliseconds + Random.Shared.Next(0, 101));
                await Task.Delay(delay, timeProvider, stoppingToken);
            }
        }
    }
}
