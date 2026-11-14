using Ledger.Adapters.Persistence.ReadModel;
using Npgsql;

namespace Ledger.Worker.Configuration;

internal sealed record WorkerRuntime(
    NpgsqlDataSource LedgerDataSource,
    NpgsqlDataSource ReadDataSource,
    PostgresReadModelStore ReadModelStore);
