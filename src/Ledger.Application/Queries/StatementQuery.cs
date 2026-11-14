namespace Ledger.Application;

public sealed record StatementQuery(
    Guid AccountId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Cursor,
    int Limit);
