using Ledger.Domain;
using Xunit;

namespace Ledger.ArchitectureTests;

public sealed class DependencyRulesTests
{
    private static readonly string[] ForbiddenInfrastructurePrefixes =
    [
        "Confluent.Kafka",
        "Microsoft.AspNetCore",
        "Npgsql",
        "Scalar"
    ];

    [Fact]
    public void DomainDoesNotReferenceApplicationOrInfrastructure()
    {
        string[] references = typeof(AccountAggregate).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, reference => reference.StartsWith("Ledger.Application", StringComparison.Ordinal));
        Assert.DoesNotContain(references, IsInfrastructureReference);
    }

    [Fact]
    public void ApplicationDoesNotReferenceAdaptersOrEntrypoints()
    {
        string[] references = typeof(Ledger.Application.LedgerCommandService).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, reference => reference.StartsWith("Ledger.Adapters", StringComparison.Ordinal));
        Assert.DoesNotContain(references, reference => reference is "Ledger.Api" or "Query.Api" or "Ledger.Worker");
        Assert.DoesNotContain(references, IsInfrastructureReference);
    }

    [Fact]
    public void OutboundAdaptersDoNotReferenceEntrypoints()
    {
        string[] references = typeof(Ledger.Adapters.Persistence.Ledger.PostgresLedgerStore).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, reference => reference is "Ledger.Api" or "Query.Api" or "Ledger.Worker");
        Assert.Contains(references, reference => reference == "Ledger.Application");
        Assert.Contains(references, reference => reference == "Ledger.Domain");
    }

    private static bool IsInfrastructureReference(string reference) =>
        ForbiddenInfrastructurePrefixes.Any(prefix => reference.StartsWith(prefix, StringComparison.Ordinal));
}
