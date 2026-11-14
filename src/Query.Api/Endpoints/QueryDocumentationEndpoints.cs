using Scalar.AspNetCore;

namespace Query.Api.Endpoints;

internal static class QueryDocumentationEndpoints
{
    public static void MapQueryDocumentation(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            return;
        }

        app.MapOpenApi("/docs/query/openapi/{documentName}.json");
        app.MapScalarApiReference(
            "/docs/query/scalar",
            options => options.WithOpenApiRoutePattern("/docs/query/openapi/{documentName}.json"));
    }
}
