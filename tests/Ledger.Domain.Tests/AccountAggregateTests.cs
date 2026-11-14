using Ledger.Domain;
using Xunit;

namespace Ledger.Domain.Tests;

public sealed class AccountAggregateTests
{
    private static readonly Guid AccountId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreditAndDebitProduceExactBalanceAndSequence()
    {
        AccountAggregate account = new(AccountId);

        account.Post(MovementType.Credit, 10_000, "BRL", Now, Now, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "crédito", null);
        account.Post(MovementType.Debit, 2_500, "BRL", Now, Now.AddSeconds(1), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "débito", null);

        Assert.Equal(7_500, account.BalanceMinor);
        Assert.Equal(2, account.Sequence);
    }

    [Fact]
    public void DebitCannotMakeBalanceNegative()
    {
        AccountAggregate account = new(AccountId);

        InsufficientFundsException exception = Assert.Throws<InsufficientFundsException>(() =>
            account.Post(MovementType.Debit, 1, "BRL", Now, Now, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "débito", null));

        Assert.Equal(0, exception.BalanceMinor);
        Assert.Equal(0, account.Sequence);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AmountMustBePositive(long amount)
    {
        AccountAggregate account = new(AccountId);

        Assert.Throws<InvalidMovementException>(() =>
            account.Post(MovementType.Credit, amount, "BRL", Now, Now, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "crédito", null));
    }

    [Fact]
    public void OnlyBrlIsAccepted()
    {
        AccountAggregate account = new(AccountId);

        Assert.Throws<InvalidMovementException>(() =>
            account.Post(MovementType.Credit, 100, "USD", Now, Now, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "crédito", null));
    }

    [Fact]
    public void SnapshotAndTailEqualFullReplay()
    {
        AccountAggregate original = new(AccountId);
        List<LedgerEvent> events = [];

        for (int index = 0; index < 60; index++)
        {
            events.Add(original.Post(
                MovementType.Credit,
                100,
                "BRL",
                Now,
                Now.AddSeconds(index),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                "crédito",
                null));
        }

        AccountAggregate firstFifty = AccountAggregate.Rehydrate(AccountId, null, events.Take(50));
        AccountSnapshot snapshot = firstFifty.CreateSnapshot(Now.AddMinutes(1));
        AccountAggregate fromSnapshot = AccountAggregate.Rehydrate(AccountId, snapshot, events.Skip(50));
        AccountAggregate fullReplay = AccountAggregate.Rehydrate(AccountId, null, events);

        Assert.Equal(fullReplay.BalanceMinor, fromSnapshot.BalanceMinor);
        Assert.Equal(fullReplay.Sequence, fromSnapshot.Sequence);
        Assert.NotEmpty(snapshot.Checksum);
    }

    [Fact]
    public void ReplayRejectsSequenceGap()
    {
        LedgerEvent eventWithGap = new(
            Guid.NewGuid(),
            AccountId,
            2,
            MovementType.Credit,
            1,
            100,
            "BRL",
            Now,
            Now,
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            "crédito",
            null);

        Assert.Throws<EventSequenceException>(() => AccountAggregate.Rehydrate(AccountId, null, [eventWithGap]));
    }

    [Fact]
    public void ReplayRejectsSnapshotWithCorruptedBalance()
    {
        AccountAggregate account = new(AccountId);
        account.Post(
            MovementType.Credit,
            100,
            "BRL",
            Now,
            Now,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "crédito",
            null);
        AccountSnapshot corrupted = account.CreateSnapshot(Now) with { BalanceMinor = 999 };

        Assert.Throws<InvalidSnapshotException>(() => AccountAggregate.Rehydrate(AccountId, corrupted, []));
    }

    [Fact]
    public void ReplayRejectsUnsupportedSnapshotSchema()
    {
        AccountAggregate account = new(AccountId);
        account.Post(
            MovementType.Credit,
            100,
            "BRL",
            Now,
            Now,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "crédito",
            null);
        AccountSnapshot unsupported = account.CreateSnapshot(Now) with { SchemaVersion = 2 };

        Assert.Throws<InvalidSnapshotException>(() => AccountAggregate.Rehydrate(AccountId, unsupported, []));
    }

    [Fact]
    public void SnapshotChecksumSurvivesPostgresTimestampPrecision()
    {
        DateTimeOffset recordedAt = Now.AddTicks(7);
        AccountAggregate account = new(AccountId);
        account.Post(
            MovementType.Credit,
            100,
            "BRL",
            recordedAt,
            recordedAt,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "credit",
            null);

        AccountSnapshot snapshot = account.CreateSnapshot(recordedAt);
        DateTimeOffset postgresTimestamp = recordedAt.AddTicks(-(recordedAt.Ticks % TimeSpan.TicksPerMicrosecond));
        AccountSnapshot persisted = snapshot with { LastRecordedAt = postgresTimestamp };

        AccountAggregate rehydrated = AccountAggregate.Rehydrate(AccountId, persisted, []);

        Assert.Equal(100, rehydrated.BalanceMinor);
        Assert.Equal(1, rehydrated.Sequence);
    }
}
