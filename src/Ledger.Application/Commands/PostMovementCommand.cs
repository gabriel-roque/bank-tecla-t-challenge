using Ledger.Domain;

namespace Ledger.Application;

public sealed record PostMovementCommand(
    Guid AccountId,
    MovementType Type,
    long Amount,
    string Currency,
    DateTimeOffset EffectiveAt,
    string Description,
    string? ExternalReference,
    string ClientId,
    string IdempotencyKey,
    Guid CorrelationId);
