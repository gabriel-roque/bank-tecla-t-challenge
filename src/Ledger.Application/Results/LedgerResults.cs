using Ledger.Domain;

namespace Ledger.Application;

public sealed record MovementResult(
    Guid MovementId,
    Guid AccountId,
    long Sequence,
    MovementType Type,
    long Amount,
    string Currency,
    long BalanceAfter,
    DateTimeOffset EffectiveAt,
    DateTimeOffset RecordedAt,
    Guid CorrelationId);

public sealed record BalanceResult(
    Guid AccountId,
    long Balance,
    string Currency,
    long LastSequence,
    DateTimeOffset? ProjectedAt,
    string Consistency);

public sealed record MovementDetails(
    Guid MovementId,
    Guid AccountId,
    long Sequence,
    MovementType Type,
    long Amount,
    long BalanceAfter,
    string Currency,
    string Description,
    DateTimeOffset EffectiveAt,
    DateTimeOffset RecordedAt,
    Guid TransactionId);

public sealed record StatementPage(
    IReadOnlyList<MovementDetails> Items,
    string? NextCursor,
    long ProjectionCheckpoint);
