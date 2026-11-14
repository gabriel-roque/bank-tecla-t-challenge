using Npgsql;

namespace Query.Api.Configuration;

internal sealed record QueryApiDataSources(
    NpgsqlDataSource ReadModel,
    NpgsqlDataSource LedgerReadOnly);
