using Ledger.Application;
using Npgsql;
using Query.Api.Security;

namespace Query.Api.Endpoints;

internal static class MovementQueryEndpoints
{
    public static void MapMovementQueryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/v1/movements/{movementId:guid}",
                HandleGetMovementAsync)
            .RequireAuthorization("movements.read")
            .WithName("GetMovement")
            .Produces<MovementDetails>()
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> HandleGetMovementAsync(
        Guid movementId,
        HttpContext context,
        ILedgerQueryUseCase useCase,
        CancellationToken cancellationToken)
    {
        try
        {
            MovementDetails? movement = await useCase.GetMovementAsync(movementId, cancellationToken);
            if (movement is null || !context.User.HasAccountAccess(movement.AccountId))
            {
                return Results.NotFound();
            }

            return Results.Ok(movement);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Read Model indisponível.");
        }
    }
}
