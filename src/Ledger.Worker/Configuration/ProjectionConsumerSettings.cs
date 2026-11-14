namespace Ledger.Worker.Configuration;

internal sealed record ProjectionConsumerSettings(string BootstrapServers, string GroupId);
