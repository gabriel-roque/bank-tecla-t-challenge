using Npgsql;

namespace Ledger.Api.Endpoints;

internal static class LedgerHealthEndpoints
{
    public static void MapLedgerHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
        endpoints.MapGet(
                "/health/ready",
                async (NpgsqlDataSource dataSource, CancellationToken cancellationToken) =>
                {
                    try
                    {
                        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
                        await using NpgsqlCommand command = new("SELECT 1;", connection);
                        await command.ExecuteScalarAsync(cancellationToken);
                        return Results.Ok(new { status = "ready" });
                    }
                    catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
                    {
                        return Results.Problem(
                            statusCode: StatusCodes.Status503ServiceUnavailable,
                            title: "not ready");
                    }
                })
            .AllowAnonymous();
    }
}
