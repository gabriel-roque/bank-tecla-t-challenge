using Ledger.Domain;

namespace Ledger.Application;

public sealed record IdempotencyEntry(
    string ClientId,
    string IdempotencyKey,
    string RequestHash,
    Guid EventId,
    short ResponseStatus,
    string ResponseBody,
    DateTimeOffset CreatedAt);

public sealed record OutboxMessage(
    Guid MessageId,
    string Topic,
    Guid PartitionKey,
    long AggregateSequence,
    string Payload,
    string Headers,
    DateTimeOffset CreatedAt);

public sealed record AggregateState(AccountSnapshot? Snapshot, IReadOnlyList<LedgerEvent> Events);
