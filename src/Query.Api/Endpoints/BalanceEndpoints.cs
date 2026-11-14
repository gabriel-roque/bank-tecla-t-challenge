using Ledger.Application;
using Npgsql;
using Query.Api.Resilience;
using Query.Api.Security;

namespace Query.Api.Endpoints;

internal static class BalanceEndpoints
{
    public static void MapBalanceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/v1/accounts/{accountId:guid}/balance",
                HandleGetBalanceAsync)
            .RequireAuthorization("balance.read")
            .WithName("GetBalance")
            .Produces<BalanceResult>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> HandleGetBalanceAsync(
        Guid accountId,
        string? consistency,
        DateTimeOffset? at,
        HttpContext context,
        ILedgerQueryUseCase useCase,
        StrongFallbackGate fallbackGate,
        CancellationToken cancellationToken)
    {
        if (!context.User.HasAccountAccess(accountId))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Conta não autorizada.");
        }

        bool explicitEventual = string.Equals(consistency, "eventual", StringComparison.OrdinalIgnoreCase);
        bool strong = string.Equals(consistency, "strong", StringComparison.OrdinalIgnoreCase) || at is not null;
        if (consistency is not null && !explicitEventual && !strong)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["consistency"] = ["Use eventual ou strong."] });
        }

        if (at is not null && explicitEventual)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["consistency"] = ["Uma consulta histórica é sempre strong."] });
        }

        try
        {
            BalanceResult result = await useCase.GetBalanceAsync(accountId, strong, at, cancellationToken);
            return Results.Ok(result);
        }
        catch (Exception exception) when (!strong && exception is NpgsqlException or TimeoutException)
        {
            return await ExecuteStrongFallbackAsync(
                accountId,
                useCase,
                fallbackGate,
                cancellationToken);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Ledger indisponível.");
        }
    }

    private static async Task<IResult> ExecuteStrongFallbackAsync(
        Guid accountId,
        ILedgerQueryUseCase useCase,
        StrongFallbackGate fallbackGate,
        CancellationToken cancellationToken)
    {
        using IDisposable? lease = fallbackGate.TryAcquire();
        if (lease is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Fallback forte temporariamente limitado.");
        }

        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            BalanceResult fallback = await useCase.GetBalanceAsync(accountId, true, null, timeout.Token);
            fallbackGate.RecordSuccess();
            return Results.Ok(fallback);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or OperationCanceledException)
        {
            fallbackGate.RecordFailure();
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Saldo indisponível.");
        }
    }
}
