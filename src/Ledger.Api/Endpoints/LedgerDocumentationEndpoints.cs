using Scalar.AspNetCore;

namespace Ledger.Api.Endpoints;

internal static class LedgerDocumentationEndpoints
{
    public static void MapLedgerDocumentation(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            return;
        }

        app.MapOpenApi("/docs/ledger/openapi/{documentName}.json");
        app.MapScalarApiReference(
            "/docs/ledger/scalar",
            options => options.WithOpenApiRoutePattern("/docs/ledger/openapi/{documentName}.json"));
    }
}
