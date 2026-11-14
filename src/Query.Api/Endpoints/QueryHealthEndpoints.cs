using Npgsql;
using Query.Api.Configuration;

namespace Query.Api.Endpoints;

internal static class QueryHealthEndpoints
{
    public static void MapQueryHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
        endpoints.MapGet(
                "/health/ready",
                async (QueryApiDataSources dataSources, CancellationToken cancellationToken) =>
                {
                    try
                    {
                        await using NpgsqlConnection connection =
                            await dataSources.ReadModel.OpenConnectionAsync(cancellationToken);
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
