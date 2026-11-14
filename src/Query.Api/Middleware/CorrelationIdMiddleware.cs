namespace Query.Api.Middleware;

internal sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    private const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        string header = context.Request.Headers[HeaderName].FirstOrDefault() ?? string.Empty;
        Guid correlationId = Guid.TryParse(header, out Guid parsed) ? parsed : Guid.NewGuid();
        context.Response.Headers[HeaderName] = correlationId.ToString();
        await next(context);
    }
}
