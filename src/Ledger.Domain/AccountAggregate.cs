namespace Ledger.Domain;

public sealed class AccountAggregate
{
    public const string SupportedCurrency = "BRL";

    public AccountAggregate(Guid accountId)
    {
        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("A conta é obrigatória.", nameof(accountId));
        }

        AccountId = accountId;
    }

    public Guid AccountId { get; }

    public long Sequence { get; private set; }

    public long BalanceMinor { get; private set; }

    public DateTimeOffset? LastRecordedAt { get; private set; }

    public static AccountAggregate Rehydrate(
        Guid accountId,
        AccountSnapshot? snapshot,
        IEnumerable<LedgerEvent> events)
    {
        AccountAggregate aggregate = new(accountId);

        if (snapshot is not null)
        {
            snapshot.ValidateIntegrity();

            if (snapshot.AccountId != accountId)
            {
                throw new InvalidMovementException("O snapshot pertence a outra conta.");
            }

            aggregate.Sequence = snapshot.Sequence;
            aggregate.BalanceMinor = snapshot.BalanceMinor;
            aggregate.LastRecordedAt = snapshot.LastRecordedAt;
        }

        foreach (LedgerEvent ledgerEvent in events)
        {
            aggregate.Apply(ledgerEvent);
        }

        return aggregate;
    }

    public LedgerEvent Post(
        MovementType movementType,
        long amountMinor,
        string currency,
        DateTimeOffset effectiveAt,
        DateTimeOffset recordedAt,
        Guid eventId,
        Guid transactionId,
        Guid correlationId,
        string description,
        string? externalReference)
    {
        Validate(amountMinor, currency);

        if (movementType == MovementType.Debit && BalanceMinor < amountMinor)
        {
            throw new InsufficientFundsException(BalanceMinor, amountMinor);
        }

        LedgerEvent ledgerEvent = new(
            eventId,
            AccountId,
            Sequence + 1,
            movementType,
            1,
            amountMinor,
            SupportedCurrency,
            effectiveAt,
            recordedAt,
            transactionId,
            null,
            correlationId,
            description,
            externalReference);

        Apply(ledgerEvent);
        return ledgerEvent;
    }

    public void Apply(LedgerEvent ledgerEvent)
    {
        ledgerEvent.Validate();

        if (ledgerEvent.AccountId != AccountId)
        {
            throw new InvalidMovementException("O evento pertence a outra conta.");
        }

        long expected = Sequence + 1;
        if (ledgerEvent.Sequence != expected)
        {
            throw new EventSequenceException(expected, ledgerEvent.Sequence);
        }

        long nextBalance = checked(BalanceMinor + ledgerEvent.SignedAmount);
        if (nextBalance < 0)
        {
            throw new InsufficientFundsException(BalanceMinor, ledgerEvent.AmountMinor);
        }

        BalanceMinor = nextBalance;
        Sequence = ledgerEvent.Sequence;
        LastRecordedAt = ledgerEvent.RecordedAt;
    }

    public AccountSnapshot CreateSnapshot(DateTimeOffset createdAt)
    {
        if (Sequence == 0 || LastRecordedAt is null)
        {
            throw new InvalidOperationException("Não é possível criar snapshot de uma conta sem eventos.");
        }

        return AccountSnapshot.Create(
            AccountId,
            Sequence,
            BalanceMinor,
            createdAt,
            LastRecordedAt.Value);
    }

    private static void Validate(long amountMinor, string currency)
    {
        if (amountMinor <= 0)
        {
            throw new InvalidMovementException("O valor deve ser maior que zero.");
        }

        if (!string.Equals(currency, SupportedCurrency, StringComparison.Ordinal))
        {
            throw new InvalidMovementException("Somente BRL é aceito neste escopo.");
        }
    }
}
