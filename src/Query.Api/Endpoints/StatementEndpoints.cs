using Ledger.Application;
using Npgsql;
using Query.Api.Security;

namespace Query.Api.Endpoints;

internal static class StatementEndpoints
{
    public static void MapStatementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/v1/accounts/{accountId:guid}/statement",
                HandleGetStatementAsync)
            .RequireAuthorization("statement.read")
            .WithName("GetStatement")
            .Produces<StatementPage>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> HandleGetStatementAsync(
        Guid accountId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? cursor,
        int? limit,
        HttpContext context,
        ILedgerQueryUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!context.User.HasAccountAccess(accountId))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Conta não autorizada.");
        }

        if (from is not null && to is not null && from > to)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["from"] = ["from deve ser menor ou igual a to."] });
        }

        try
        {
            StatementPage page = await useCase.GetStatementAsync(
                new StatementQuery(accountId, from, to, cursor, limit ?? 50),
                cancellationToken);
            return Results.Ok(page);
        }
        catch (ArgumentException exception)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["cursor"] = [exception.Message] });
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Read Model indisponível.");
        }
    }
}
