using Ledger.Worker.State;

namespace Ledger.Worker.Endpoints;

internal static class WorkerHealthEndpoints
{
    public static void MapWorkerHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        endpoints.MapGet(
            "/health/ready",
            (WorkerState state) => state.FatalError is null
                ? Results.Ok(new
                {
                    status = "ready",
                    publisherLastSuccessAt = state.PublisherLastSuccessAt,
                    consumerLastSuccessAt = state.ConsumerLastSuccessAt
                })
                : Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: state.FatalError));
    }
}
