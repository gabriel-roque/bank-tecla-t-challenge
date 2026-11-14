using Ledger.Adapters.Persistence.Migrations;
using Ledger.Api.Configuration;
using Ledger.Api.Endpoints;
using Ledger.Api.Middleware;
using Npgsql;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddLedgerApi(builder.Configuration);

WebApplication app = builder.Build();
NpgsqlDataSource ledgerDataSource = app.Services.GetRequiredService<NpgsqlDataSource>();

if (builder.Configuration.GetValue<bool>("MigrateOnly"))
{
    await DatabaseMigrator.MigrateLedgerAsync(ledgerDataSource, CancellationToken.None);
    return;
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

app.MapLedgerDocumentation();
app.MapMovementEndpoints();
app.MapLedgerHealthEndpoints();

await app.RunAsync();
