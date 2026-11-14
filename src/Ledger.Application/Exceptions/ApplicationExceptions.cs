namespace Ledger.Application;

public sealed class ConcurrencyConflictException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class IdempotencyConflictException()
    : Exception("A Idempotency-Key já foi usada com outro payload.");

public sealed class ProjectionSequenceGapException(Guid accountId, long expected, long actual)
    : Exception($"Lacuna de projeção na conta {accountId}: esperada {expected}, recebida {actual}.")
{
    public Guid AccountId { get; } = accountId;

    public long Expected { get; } = expected;

    public long Actual { get; } = actual;
}

public sealed class UnrecoverableProjectionGapException(
    Guid accountId,
    long expected,
    long actual,
    Exception innerException)
    : Exception(
        $"Lacuna irrecuperável na conta {accountId}: esperada {expected}, recebida {actual}.",
        innerException)
{
    public Guid AccountId { get; } = accountId;

    public long Expected { get; } = expected;

    public long Actual { get; } = actual;
}
