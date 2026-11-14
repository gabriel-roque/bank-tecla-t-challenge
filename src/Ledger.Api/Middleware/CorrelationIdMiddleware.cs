namespace Ledger.Api.Middleware;

internal sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string ItemKey = "CorrelationId";
    private const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        string header = context.Request.Headers[HeaderName].FirstOrDefault() ?? string.Empty;
        Guid correlationId = Guid.TryParse(header, out Guid parsed) ? parsed : Guid.NewGuid();
        context.Items[ItemKey] = correlationId;
        context.Response.Headers[HeaderName] = correlationId.ToString();
        await next(context);
    }
}
