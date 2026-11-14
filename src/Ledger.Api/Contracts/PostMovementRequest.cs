namespace Ledger.Api.Contracts;

internal sealed record PostMovementRequest(
    string Type,
    long Amount,
    string Currency,
    DateTimeOffset EffectiveAt,
    string Description,
    string? ExternalReference);
