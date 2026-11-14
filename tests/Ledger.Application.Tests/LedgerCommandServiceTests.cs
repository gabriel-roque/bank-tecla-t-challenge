using Ledger.Application;
using Ledger.Domain;
using Xunit;

namespace Ledger.Application.Tests;

public sealed class LedgerCommandServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SameIdempotencyKeyReturnsOriginalResult()
    {
        InMemoryLedgerStore store = new();
        LedgerCommandService service = new(store, new FixedTimeProvider(Now));
        PostMovementCommand command = CreateCommand("same-key");

        MovementResult first = await service.PostAsync(command, TestContext.Current.CancellationToken);
        MovementResult second = await service.PostAsync(command, TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
        Assert.Single(store.Events);
        Assert.Single(store.Outbox);
    }

    [Fact]
    public async Task SameKeyWithDifferentPayloadIsRejected()
    {
        InMemoryLedgerStore store = new();
        LedgerCommandService service = new(store, new FixedTimeProvider(Now));
        await service.PostAsync(CreateCommand("same-key"), TestContext.Current.CancellationToken);

        PostMovementCommand changed = CreateCommand("same-key") with { Amount = 999 };

        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            service.PostAsync(changed, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void UnknownEventTypeIsRejected()
    {
        LedgerEventEnvelope envelope = CreateEnvelope() with { EventType = "BalanceAdjusted" };

        Assert.Throws<InvalidLedgerEventException>(envelope.Validate);
    }

    [Fact]
    public void UnknownEventVersionIsRejected()
    {
        LedgerEventEnvelope envelope = CreateEnvelope() with { EventVersion = 2 };

        Assert.Throws<InvalidLedgerEventException>(envelope.Validate);
    }

    private static PostMovementCommand CreateCommand(string key) => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        MovementType.Credit,
        1_000,
        "BRL",
        Now,
        "crédito",
        "external-1",
        "test-client",
        key,
        Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private static LedgerEventEnvelope CreateEnvelope() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        1,
        "CreditPosted",
        LedgerEvent.CurrentEventVersion,
        100,
        AccountAggregate.SupportedCurrency,
        Now,
        Now,
        Guid.NewGuid(),
        Guid.NewGuid(),
        "crédito",
        null);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class InMemoryLedgerStore : ILedgerStore, ILedgerSession
    {
        public List<LedgerEvent> Events { get; } = [];

        public List<OutboxMessage> Outbox { get; } = [];

        private List<AccountSnapshot> Snapshots { get; } = [];

        private List<IdempotencyEntry> Idempotency { get; } = [];

        public Task<T> ExecuteAsync<T>(
            Func<ILedgerSession, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken) => operation(this, cancellationToken);

        public Task<AggregateState> LoadAggregateAsync(
            Guid accountId,
            DateTimeOffset? at,
            CancellationToken cancellationToken)
        {
            AccountSnapshot? snapshot = Snapshots.Where(item => item.AccountId == accountId).MaxBy(item => item.Sequence);
            IReadOnlyList<LedgerEvent> events = Events
                .Where(item => item.AccountId == accountId && item.Sequence > (snapshot?.Sequence ?? 0))
                .OrderBy(item => item.Sequence)
                .ToList();
            return Task.FromResult(new AggregateState(snapshot, events));
        }

        public Task<IReadOnlyList<LedgerEvent>> ReadEventsAsync(
            Guid accountId,
            long fromSequence,
            long toSequence,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<LedgerEvent>>(
            Events.Where(item => item.AccountId == accountId && item.Sequence >= fromSequence && item.Sequence <= toSequence).ToList());

        public Task<IdempotencyEntry?> FindIdempotencyAsync(
            string clientId,
            string idempotencyKey,
            CancellationToken cancellationToken) => Task.FromResult(
            Idempotency.SingleOrDefault(item => item.ClientId == clientId && item.IdempotencyKey == idempotencyKey));

        public Task AppendEventAsync(LedgerEvent ledgerEvent, CancellationToken cancellationToken)
        {
            Events.Add(ledgerEvent);
            return Task.CompletedTask;
        }

        public Task SaveSnapshotAsync(AccountSnapshot snapshot, CancellationToken cancellationToken)
        {
            Snapshots.Add(snapshot);
            return Task.CompletedTask;
        }

        public Task SaveIdempotencyAsync(IdempotencyEntry entry, CancellationToken cancellationToken)
        {
            Idempotency.Add(entry);
            return Task.CompletedTask;
        }

        public Task SaveOutboxAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            Outbox.Add(message);
            return Task.CompletedTask;
        }
    }
}

internal static class TestContext
{
    public static TestRunContext Current { get; } = new();
}

internal sealed class TestRunContext
{
    public CancellationToken CancellationToken => CancellationToken.None;
}
