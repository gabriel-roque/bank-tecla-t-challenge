using DotNet.Testcontainers.Configurations;
using Ledger.Adapters.Persistence.Ledger;
using Ledger.Adapters.Persistence.Migrations;
using Ledger.Adapters.Persistence.ReadModel;
using Ledger.Application;
using Ledger.Domain;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Ledger.IntegrationTests;

public sealed class PostgresIntegrationTests : IAsyncLifetime
{
    static PostgresIntegrationTests()
    {
        // Every container is disposed explicitly by the fixture. Avoid making the
        // suite depend on pulling the optional Ryuk helper image from Docker Hub.
        TestcontainersSettings.ResourceReaperEnabled = false;
    }

    private readonly PostgreSqlContainer ledgerContainer = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("ledger")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();
    private readonly PostgreSqlContainer readContainer = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("ledger_read")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();
    private NpgsqlDataSource ledgerDataSource = null!;
    private NpgsqlDataSource readDataSource = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(ledgerContainer.StartAsync(), readContainer.StartAsync());
        ledgerDataSource = NpgsqlDataSource.Create(ledgerContainer.GetConnectionString());
        readDataSource = NpgsqlDataSource.Create(readContainer.GetConnectionString());
        await DatabaseMigrator.MigrateLedgerAsync(ledgerDataSource, TestContext.Current.CancellationToken);
        await DatabaseMigrator.MigrateReadAsync(readDataSource, TestContext.Current.CancellationToken);
    }

    public async Task DisposeAsync()
    {
        await ledgerDataSource.DisposeAsync();
        await readDataSource.DisposeAsync();
        await Task.WhenAll(ledgerContainer.DisposeAsync().AsTask(), readContainer.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task EventIdempotencyAndOutboxAreCommittedTogether()
    {
        PostgresLedgerStore store = new(ledgerDataSource);
        LedgerCommandService service = new(store, TimeProvider.System);
        PostMovementCommand command = Credit(Guid.NewGuid(), "idem-1", 10_000);

        MovementResult first = await service.PostAsync(command, TestContext.Current.CancellationToken);
        MovementResult repeated = await service.PostAsync(command, TestContext.Current.CancellationToken);

        Assert.Equal(first.MovementId, repeated.MovementId);
        Assert.Equal(1, await CountAsync("ledger_events"));
        Assert.Equal(1, await CountAsync("idempotency_requests"));
        Assert.Equal(1, await CountAsync("outbox_messages"));
    }

    [Fact]
    public async Task ConcurrentDebitsNeverMakeBalanceNegative()
    {
        Guid accountId = Guid.NewGuid();
        PostgresLedgerStore store = new(ledgerDataSource);
        LedgerCommandService service = new(store, TimeProvider.System);
        await service.PostAsync(Credit(accountId, "seed", 100), TestContext.Current.CancellationToken);
        PostMovementCommand firstDebit = Debit(accountId, "debit-1", 80);
        PostMovementCommand secondDebit = Debit(accountId, "debit-2", 80);

        Task<MovementResult> first = service.PostAsync(firstDebit, TestContext.Current.CancellationToken);
        Task<MovementResult> second = service.PostAsync(secondDebit, TestContext.Current.CancellationToken);
        Task[] tasks = [first, second];
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (InsufficientFundsException)
        {
            // Uma das duas operações deve ser rejeitada após recarregar a versão vencedora.
        }

        AggregateState state = await store.LoadAggregateAsync(accountId, null, TestContext.Current.CancellationToken);
        AccountAggregate aggregate = AccountAggregate.Rehydrate(accountId, state.Snapshot, state.Events);
        Assert.Equal(20, aggregate.BalanceMinor);
        Assert.Equal(2, aggregate.Sequence);
    }

    [Fact]
    public async Task ProjectionIsIdempotentAndMatchesStrongBalance()
    {
        Guid accountId = Guid.NewGuid();
        PostgresLedgerStore ledger = new(ledgerDataSource);
        PostgresReadModelStore read = new(readDataSource, TimeProvider.System);
        LedgerCommandService service = new(ledger, TimeProvider.System);
        MovementResult movement = await service.PostAsync(
            Credit(accountId, "projection", 1_234),
            TestContext.Current.CancellationToken);
        IReadOnlyList<LedgerEvent> events = await ledger.ReadEventsAsync(accountId, 1, 1, TestContext.Current.CancellationToken);
        LedgerEventEnvelope envelope = LedgerCommandService.ToEnvelope(Assert.Single(events));

        ProjectionApplyResult first = await read.ApplyAsync(envelope, TestContext.Current.CancellationToken);
        ProjectionApplyResult duplicate = await read.ApplyAsync(envelope, TestContext.Current.CancellationToken);
        BalanceResult? balance = await read.GetBalanceAsync(accountId, TestContext.Current.CancellationToken);

        Assert.Equal(ProjectionApplyResult.Applied, first);
        Assert.Equal(ProjectionApplyResult.Duplicate, duplicate);
        Assert.NotNull(balance);
        Assert.Equal(movement.BalanceAfter, balance.Balance);
        Assert.Equal(1, await CountReadAsync("statement_entries"));
    }

    [Fact]
    public async Task LockedFirstSequenceDoesNotMakeLaterSequenceEligible()
    {
        Guid accountA = Guid.NewGuid();
        Guid accountB = Guid.NewGuid();
        PostgresLedgerStore ledger = new(ledgerDataSource);
        LedgerCommandService service = new(ledger, TimeProvider.System);
        await service.PostAsync(Credit(accountA, "a-1", 100), TestContext.Current.CancellationToken);
        await service.PostAsync(Credit(accountA, "a-2", 100), TestContext.Current.CancellationToken);
        await service.PostAsync(Credit(accountA, "a-3", 100), TestContext.Current.CancellationToken);
        await service.PostAsync(Credit(accountB, "b-1", 100), TestContext.Current.CancellationToken);

        await using NpgsqlConnection locker = await ledgerDataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using NpgsqlTransaction lockerTransaction = await locker.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (NpgsqlCommand lockCommand = new(
                         "SELECT message_id FROM outbox_messages WHERE partition_key = @account_id AND aggregate_sequence = 1 FOR UPDATE;",
                         locker,
                         lockerTransaction))
        {
            lockCommand.Parameters.AddWithValue("account_id", accountA);
            await lockCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        const string candidateSql = """
            SELECT candidate.partition_key, candidate.aggregate_sequence
            FROM outbox_messages AS candidate
            WHERE candidate.published_at IS NULL
              AND NOT EXISTS (
                  SELECT 1 FROM outbox_messages AS previous
                  WHERE previous.partition_key = candidate.partition_key
                    AND previous.published_at IS NULL
                    AND previous.aggregate_sequence < candidate.aggregate_sequence)
            ORDER BY candidate.created_at, candidate.message_id
            FOR UPDATE OF candidate SKIP LOCKED
            LIMIT 10;
            """;
        await using NpgsqlConnection competitor = await ledgerDataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using NpgsqlTransaction competitorTransaction = await competitor.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using NpgsqlCommand candidateCommand = new(candidateSql, competitor, competitorTransaction);
        List<(Guid AccountId, long Sequence)> selected = [];
        await using (NpgsqlDataReader reader = await candidateCommand.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                selected.Add((reader.GetGuid(0), reader.GetInt64(1)));
            }
        }

        Assert.Contains(selected, item => item.AccountId == accountB && item.Sequence == 1);
        Assert.DoesNotContain(selected, item => item.AccountId == accountA);
        await competitorTransaction.RollbackAsync(TestContext.Current.CancellationToken);
        await lockerTransaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SnapshotAtFiftyAndHistoricalBalanceUseRecordedTimeline()
    {
        Guid accountId = Guid.NewGuid();
        DateTimeOffset initialTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(7);
        TestTimeProvider clock = new(initialTime);
        PostgresLedgerStore store = new(ledgerDataSource);
        LedgerCommandService service = new(store, clock);

        MovementResult first = await service.PostAsync(
            Credit(accountId, "snapshot-1", 100),
            TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(1));

        for (int sequence = 2; sequence <= 50; sequence++)
        {
            await service.PostAsync(
                Credit(accountId, $"snapshot-{sequence}", 100),
                TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        AggregateState historicalState = await store.LoadAggregateAsync(
            accountId,
            first.RecordedAt,
            TestContext.Current.CancellationToken);
        AccountAggregate historical = AccountAggregate.Rehydrate(
            accountId,
            historicalState.Snapshot,
            historicalState.Events);
        AggregateState currentState = await store.LoadAggregateAsync(
            accountId,
            null,
            TestContext.Current.CancellationToken);
        AccountAggregate current = AccountAggregate.Rehydrate(
            accountId,
            currentState.Snapshot,
            currentState.Events);

        Assert.Equal(100, historical.BalanceMinor);
        Assert.Equal(1, historical.Sequence);
        Assert.Equal(5_000, current.BalanceMinor);
        Assert.Equal(50, current.Sequence);
        Assert.Equal(1, await CountAsync("account_snapshots"));
    }

    [Fact]
    public async Task StatementUsesStableCursorPagination()
    {
        Guid accountId = Guid.NewGuid();
        TestTimeProvider clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        PostgresLedgerStore ledger = new(ledgerDataSource);
        PostgresReadModelStore read = new(readDataSource, clock);
        LedgerCommandService service = new(ledger, clock);

        for (int sequence = 1; sequence <= 3; sequence++)
        {
            await service.PostAsync(
                Credit(accountId, $"page-{sequence}", 100),
                TestContext.Current.CancellationToken);
            IReadOnlyList<LedgerEvent> events = await ledger.ReadEventsAsync(
                accountId,
                sequence,
                sequence,
                TestContext.Current.CancellationToken);
            await read.ApplyAsync(
                LedgerCommandService.ToEnvelope(Assert.Single(events)),
                TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        StatementPage firstPage = await read.GetStatementAsync(
            new StatementQuery(accountId, null, null, null, 2),
            TestContext.Current.CancellationToken);
        StatementPage secondPage = await read.GetStatementAsync(
            new StatementQuery(accountId, null, null, firstPage.NextCursor, 2),
            TestContext.Current.CancellationToken);

        Assert.Equal(new long[] { 3, 2 }, firstPage.Items.Select(item => item.Sequence));
        Assert.NotNull(firstPage.NextCursor);
        Assert.Equal(new long[] { 1 }, secondPage.Items.Select(item => item.Sequence));
        Assert.Null(secondPage.NextCursor);
        Assert.Equal(3, secondPage.ProjectionCheckpoint);
    }

    private static PostMovementCommand Credit(Guid accountId, string idempotencyKey, long amount) =>
        Movement(accountId, idempotencyKey, amount, MovementType.Credit);

    private static PostMovementCommand Debit(Guid accountId, string idempotencyKey, long amount) =>
        Movement(accountId, idempotencyKey, amount, MovementType.Debit);

    private static PostMovementCommand Movement(Guid accountId, string key, long amount, MovementType type) => new(
        accountId,
        type,
        amount,
        "BRL",
        DateTimeOffset.UtcNow,
        type.ToString(),
        null,
        "integration-tests",
        key,
        Guid.NewGuid());

    private async Task<long> CountAsync(string table)
    {
        await using NpgsqlConnection connection = await ledgerDataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using NpgsqlCommand command = new($"SELECT COUNT(*) FROM {table};", connection);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private async Task<long> CountReadAsync(string table)
    {
        await using NpgsqlConnection connection = await readDataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using NpgsqlCommand command = new($"SELECT COUNT(*) FROM {table};", connection);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
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

internal sealed class TestTimeProvider(DateTimeOffset current) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => current;

    public void Advance(TimeSpan duration) => current = current.Add(duration);
}
