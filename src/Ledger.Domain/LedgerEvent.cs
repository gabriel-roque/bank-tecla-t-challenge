namespace Ledger.Domain;

public sealed record LedgerEvent(
    Guid EventId,
    Guid AccountId,
    long Sequence,
    MovementType MovementType,
    short EventVersion,
    long AmountMinor,
    string Currency,
    DateTimeOffset EffectiveAt,
    DateTimeOffset RecordedAt,
    Guid TransactionId,
    Guid? CausationId,
    Guid CorrelationId,
    string Description,
    string? ExternalReference)
{
    public const short CurrentEventVersion = 1;

    public string EventType => MovementType switch
    {
        MovementType.Credit => "CreditPosted",
        MovementType.Debit => "DebitPosted",
        _ => throw new InvalidLedgerEventException($"Tipo de movimentação inválido: {MovementType}.")
    };

    public long SignedAmount => MovementType switch
    {
        MovementType.Credit => AmountMinor,
        MovementType.Debit => -AmountMinor,
        _ => throw new InvalidLedgerEventException($"Tipo de movimentação inválido: {MovementType}.")
    };

    public void Validate()
    {
        if (EventId == Guid.Empty || AccountId == Guid.Empty || TransactionId == Guid.Empty || CorrelationId == Guid.Empty)
        {
            throw new InvalidLedgerEventException("O evento contém identificadores obrigatórios inválidos.");
        }

        if (EventVersion != CurrentEventVersion)
        {
            throw new InvalidLedgerEventException($"Versão de evento não suportada: {EventVersion}.");
        }

        if (Sequence <= 0 || AmountMinor <= 0)
        {
            throw new InvalidLedgerEventException("O evento contém sequência ou valor inválido.");
        }

        if (!string.Equals(Currency, AccountAggregate.SupportedCurrency, StringComparison.Ordinal))
        {
            throw new InvalidLedgerEventException($"Moeda de evento não suportada: {Currency}.");
        }

        _ = EventType;
    }
}
