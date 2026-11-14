using System.Text.Json;
using Confluent.Kafka;
using Ledger.Application;
using Ledger.Domain;
using Ledger.Worker.Configuration;
using Ledger.Worker.State;

namespace Ledger.Worker.Services;

internal sealed class ProjectionConsumerBackgroundService(
    ProjectionConsumerSettings settings,
    IProjectLedgerEventUseCase projectLedgerEvent,
    WorkerState state,
    TimeProvider timeProvider,
    ILogger<ProjectionConsumerBackgroundService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ConsumerConfig config = new()
        {
            BootstrapServers = settings.BootstrapServers,
            GroupId = settings.GroupId,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            AllowAutoCreateTopics = false,
            SessionTimeoutMs = 10_000,
            MaxPollIntervalMs = 300_000
        };

        using IConsumer<string, string> consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(LedgerCommandService.TopicName);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? consumed = null;
            try
            {
                consumed = consumer.Consume(stoppingToken);
                LedgerEventEnvelope envelope = JsonSerializer.Deserialize<LedgerEventEnvelope>(consumed.Message.Value, JsonOptions)
                    ?? throw new InvalidLedgerEventException("Envelope Kafka inválido.");

                await projectLedgerEvent.ProjectAsync(envelope, stoppingToken);
                consumer.Commit(consumed);
                state.ConsumerLastSuccessAt = timeProvider.GetUtcNow();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (UnrecoverableProjectionGapException exception) when (consumed is not null)
            {
                PausePartition(consumer, consumed, exception, "lacuna irrecuperável");
            }
            catch (Exception exception) when (
                consumed is not null && exception is InvalidLedgerEventException or JsonException)
            {
                PausePartition(consumer, consumed, exception, "evento inválido");
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Falha recuperável ao projetar evento.");
                if (consumed is not null)
                {
                    consumer.Seek(consumed.TopicPartitionOffset);
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(1_000 + Random.Shared.Next(0, 251)),
                    timeProvider,
                    stoppingToken);
            }
        }

        consumer.Close();
    }

    private void PausePartition(
        IConsumer<string, string> consumer,
        ConsumeResult<string, string> consumed,
        Exception exception,
        string reason)
    {
        state.FatalError = exception.Message;
        consumer.Pause([consumed.TopicPartition]);
        logger.LogError(
            exception,
            "Partição {Partition} pausada por {Reason}.",
            consumed.TopicPartition,
            reason);
    }
}
