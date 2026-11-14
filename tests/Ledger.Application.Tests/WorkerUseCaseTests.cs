using Ledger.Application;
using Ledger.Domain;
using Xunit;

namespace Ledger.Application.Tests;

public sealed class WorkerUseCaseTests
{
    private static readonly Guid AccountId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OutboxPublishingDelegatesToOutboundPort()
    {
        FakeOutboxPublisher publisher = new(3);
        OutboxPublishingService service = new(publisher);

        int published = await service.PublishPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, published);
        Assert.Equal(1, publisher.Calls);
    }

    [Fact]
    public async Task ProjectionRecoversMissingEventsBeforeApplyingCurrentEvent()
    {
        FakeReadModelStore readModel = new();
        FakeLedgerStore ledger = new([CreateEvent(1), CreateEvent(2)]);
        LedgerProjectionService service = new(readModel, ledger);

        await service.ProjectAsync(
            LedgerCommandService.ToEnvelope(CreateEvent(3)),
            TestContext.Current.CancellationToken);

        Assert.Equal([1L, 2L, 3L], readModel.AppliedSequences);
        Assert.Equal((1L, 2L), ledger.LastRequestedRange);
    }

    [Fact]
    public async Task ProjectionRejectsAnUnrecoverableGap()
    {
        FakeReadModelStore readModel = new();
        FakeLedgerStore ledger = new([CreateEvent(1)]);
        LedgerProjectionService service = new(readModel, ledger);

        UnrecoverableProjectionGapException exception = await Assert.ThrowsAsync<UnrecoverableProjectionGapException>(
            () => service.ProjectAsync(
                LedgerCommandService.ToEnvelope(CreateEvent(3)),
                TestContext.Current.CancellationToken));

        Assert.Equal(AccountId, exception.AccountId);
        Assert.Equal(1, exception.Expected);
        Assert.Equal(3, exception.Actual);
        Assert.Empty(readModel.AppliedSequences);
    }

    private static LedgerEvent CreateEvent(long sequence) => new(
        Guid.NewGuid(),
        AccountId,
        sequence,
        MovementType.Credit,
        LedgerEvent.CurrentEventVersion,
        100,
        AccountAggregate.SupportedCurrency,
        Now,
        Now.AddSeconds(sequence),
        Guid.NewGuid(),
        null,
        Guid.NewGuid(),
        $"crédito {sequence}",
        null);

    private sealed class FakeOutboxPublisher(int result) : IOutboxPublisher
    {
        public int Calls { get; private set; }

        public Task<int> PublishPendingAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeLedgerStore(IReadOnlyList<LedgerEvent> events) : ILedgerStore
    {
        public (long From, long To)? LastRequestedRange { get; private set; }

        public Task<T> ExecuteAsync<T>(
            Func<ILedgerSession, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AggregateState> LoadAggregateAsync(
            Guid accountId,
            DateTimeOffset? at,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<LedgerEvent>> ReadEventsAsync(
            Guid accountId,
            long fromSequence,
            long toSequence,
            CancellationToken cancellationToken)
        {
            LastRequestedRange = (fromSequence, toSequence);
            return Task.FromResult<IReadOnlyList<LedgerEvent>>(
                events.Where(item =>
                        item.AccountId == accountId &&
                        item.Sequence >= fromSequence &&
                        item.Sequence <= toSequence)
                    .OrderBy(item => item.Sequence)
                    .ToList());
        }
    }

    private sealed class FakeReadModelStore : IReadModelStore
    {
        public List<long> AppliedSequences { get; } = [];

        public Task<BalanceResult?> GetBalanceAsync(
            Guid accountId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MovementDetails?> GetMovementAsync(
            Guid movementId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StatementPage> GetStatementAsync(
            StatementQuery query,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProjectionApplyResult> ApplyAsync(
            LedgerEventEnvelope envelope,
            CancellationToken cancellationToken)
        {
            long expected = AppliedSequences.Count + 1L;
            if (envelope.Sequence != expected)
            {
                throw new ProjectionSequenceGapException(envelope.AccountId, expected, envelope.Sequence);
            }

            AppliedSequences.Add(envelope.Sequence);
            return Task.FromResult(ProjectionApplyResult.Applied);
        }
    }
}
