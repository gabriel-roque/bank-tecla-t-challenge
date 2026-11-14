using Ledger.Adapters.Persistence.Migrations;
using Ledger.Adapters.Persistence.ReadModel;
using Ledger.Worker.Configuration;
using Ledger.Worker.Endpoints;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddLedgerWorker(builder.Configuration);

WebApplication app = builder.Build();
WorkerRuntime runtime = app.Services.GetRequiredService<WorkerRuntime>();

if (builder.Configuration.GetValue<bool>("MigrateOnly"))
{
    await DatabaseMigrator.MigrateReadAsync(runtime.ReadDataSource, CancellationToken.None);
    return;
}

if (builder.Configuration.GetValue<bool>("ReplayOnly"))
{
    await DatabaseMigrator.MigrateReadAsync(runtime.ReadDataSource, CancellationToken.None);
    PostgresReadModelRebuilder rebuilder = new(
        runtime.LedgerDataSource,
        runtime.ReadDataSource,
        runtime.ReadModelStore);
    long rebuilt = await rebuilder.RebuildAsync(CancellationToken.None);
    app.Logger.LogInformation("Read Model reconstruído com {EventCount} eventos.", rebuilt);
    return;
}

app.MapWorkerHealthEndpoints();
await app.RunAsync();
