using Confluent.Kafka;
using Ledger.Adapters.Messaging.Kafka;
using Ledger.Adapters.Persistence.Ledger;
using Ledger.Adapters.Persistence.ReadModel;
using Ledger.Application;
using Ledger.Worker.Services;
using Ledger.Worker.State;
using Npgsql;

namespace Ledger.Worker.Configuration;

internal static class WorkerServiceCollectionExtensions
{
    public static IServiceCollection AddLedgerWorker(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);

        string ledgerConnectionString = configuration.GetConnectionString("Ledger")
            ?? throw new InvalidOperationException("ConnectionStrings:Ledger é obrigatória.");
        string readConnectionString = configuration.GetConnectionString("Read")
            ?? throw new InvalidOperationException("ConnectionStrings:Read é obrigatória.");
        string bootstrapServers = configuration["Kafka:BootstrapServers"] ?? "localhost:9092";
        string groupId = configuration["Kafka:GroupId"] ?? "account-balance-projection-v1";
        int batchSize = configuration.GetValue("Outbox:BatchSize", 32);

        NpgsqlDataSource ledgerDataSource = NpgsqlDataSource.Create(ledgerConnectionString);
        NpgsqlDataSource readDataSource = NpgsqlDataSource.Create(readConnectionString);
        PostgresLedgerStore ledgerStore = new(ledgerDataSource);
        PostgresReadModelStore readModelStore = new(readDataSource, TimeProvider.System);
        IProducer<string, string> producer = CreateProducer(bootstrapServers);

        services.AddSingleton(new WorkerRuntime(ledgerDataSource, readDataSource, readModelStore));
        services.AddSingleton<ILedgerStore>(ledgerStore);
        services.AddSingleton<IReadModelStore>(readModelStore);
        services.AddSingleton(producer);
        services.AddSingleton<IOutboxPublisher>(
            new PostgresOutboxPublisher(ledgerDataSource, producer, batchSize));
        services.AddSingleton<IPublishPendingOutboxUseCase, OutboxPublishingService>();
        services.AddSingleton<IProjectLedgerEventUseCase, LedgerProjectionService>();
        services.AddSingleton(new ProjectionConsumerSettings(bootstrapServers, groupId));
        services.AddSingleton<WorkerState>();
        services.AddHostedService<OutboxPublisherBackgroundService>();
        services.AddHostedService<ProjectionConsumerBackgroundService>();
        return services;
    }

    private static IProducer<string, string> CreateProducer(string bootstrapServers) =>
        new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageTimeoutMs = 3_000,
            SocketTimeoutMs = 3_000,
            MaxInFlight = 5
        }).Build();
}
