using System.Security.Claims;
using Ledger.Api.Contracts;
using Ledger.Api.Middleware;
using Ledger.Api.Security;
using Ledger.Application;
using Ledger.Domain;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Ledger.Api.Endpoints;

internal static class MovementEndpoints
{
    public static void MapMovementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/v1/accounts/{accountId:guid}/movements",
                HandlePostMovementAsync)
            .RequireAuthorization("movements.write")
            .WithName("PostMovement")
            .Produces<MovementResult>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> HandlePostMovementAsync(
        Guid accountId,
        PostMovementRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        HttpContext context,
        IPostMovementUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!context.User.HasAccountAccess(accountId))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Conta não autorizada.");
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["Idempotency-Key"] = ["Header obrigatório com até 100 caracteres."] });
        }

        if (!Enum.TryParse(request.Type, true, out MovementType movementType))
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["type"] = ["Use CREDIT ou DEBIT."] });
        }

        if (string.IsNullOrEmpty(request.Description) || request.Description.Length > 255)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["description"] = ["Informe de 1 a 255 caracteres."] });
        }

        string clientId = context.User.FindFirstValue("client_id")
            ?? throw new InvalidOperationException("Token validado sem client_id.");
        Guid correlationId = (Guid)context.Items[CorrelationIdMiddleware.ItemKey]!;

        try
        {
            MovementResult result = await useCase.PostAsync(
                new PostMovementCommand(
                    accountId,
                    movementType,
                    request.Amount,
                    request.Currency,
                    request.EffectiveAt,
                    request.Description,
                    request.ExternalReference,
                    clientId,
                    idempotencyKey,
                    correlationId),
                cancellationToken);
            return Results.Created($"/v1/movements/{result.MovementId}", result);
        }
        catch (IdempotencyConflictException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: exception.Message);
        }
        catch (InsufficientFundsException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Saldo insuficiente.",
                detail: exception.Message);
        }
        catch (InvalidMovementException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Movimentação inválida.",
                detail: exception.Message);
        }
        catch (ConcurrencyConflictException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Conflito concorrente.",
                detail: exception.Message);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Ledger indisponível.");
        }
    }
}
