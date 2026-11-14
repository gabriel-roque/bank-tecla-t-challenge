using Query.Api.Configuration;
using Query.Api.Endpoints;
using Query.Api.Middleware;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddQueryApi(builder.Configuration);

WebApplication app = builder.Build();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

app.MapQueryDocumentation();
app.MapMovementQueryEndpoints();
app.MapBalanceEndpoints();
app.MapStatementEndpoints();
app.MapQueryHealthEndpoints();

await app.RunAsync();
