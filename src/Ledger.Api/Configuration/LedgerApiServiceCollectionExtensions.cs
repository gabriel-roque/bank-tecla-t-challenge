using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Json.Serialization;
using Ledger.Adapters.Persistence.Ledger;
using Ledger.Api.Security;
using Ledger.Application;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Npgsql;

namespace Ledger.Api.Configuration;

internal static class LedgerApiServiceCollectionExtensions
{
    public static IServiceCollection AddLedgerApi(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.AddOpenApi(ConfigureOpenApi);
        services.AddHealthChecks();
        services.AddSingleton(TimeProvider.System);

        string connectionString = configuration.GetConnectionString("Ledger")
            ?? throw new InvalidOperationException("ConnectionStrings:Ledger é obrigatória.");
        services.AddSingleton(NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<ILedgerStore, PostgresLedgerStore>();
        services.AddSingleton<IPostMovementUseCase, LedgerCommandService>();

        ConfigureAuthentication(services, configuration);
        return services;
    }

    private static void ConfigureAuthentication(IServiceCollection services, IConfiguration configuration)
    {
        string signingKey = configuration["Jwt:SigningKey"]
            ?? throw new InvalidOperationException("Jwt:SigningKey é obrigatória.");
        string issuer = configuration["Jwt:Issuer"] ?? "ledger-local";
        string audience = configuration["Jwt:Audience"] ?? "ledger-api";

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = JwtRegisteredClaimNames.Sub
                };
            });
        services.AddAuthorizationBuilder()
            .AddPolicy("movements.write", policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.HasScope("ledger.movements.write")));
    }

    private static void ConfigureOpenApi(Microsoft.AspNetCore.OpenApi.OpenApiOptions options)
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "JWT interno com scopes e, quando delegado, account_id."
            };
            document.Security ??= [];
            document.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", document, null)] = []
            });

            foreach ((string path, IOpenApiPathItem item) in document.Paths)
            {
                if (path.StartsWith("/health/", StringComparison.Ordinal) && item.Operations is not null)
                {
                    foreach (OpenApiOperation operation in item.Operations.Values)
                    {
                        operation.Security = [];
                    }
                }
            }

            return Task.CompletedTask;
        });
    }
}
