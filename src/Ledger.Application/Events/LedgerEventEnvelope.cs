using Ledger.Domain;

namespace Ledger.Application;

public sealed record LedgerEventEnvelope(
    Guid EventId,
    Guid AccountId,
    long Sequence,
    string EventType,
    short EventVersion,
    long Amount,
    string Currency,
    DateTimeOffset EffectiveAt,
    DateTimeOffset RecordedAt,
    Guid TransactionId,
    Guid CorrelationId,
    string Description,
    string? ExternalReference)
{
    public MovementType GetMovementType() => EventType switch
    {
        "CreditPosted" => MovementType.Credit,
        "DebitPosted" => MovementType.Debit,
        _ => throw new InvalidLedgerEventException($"Tipo de evento não suportado: {EventType}.")
    };

    public void Validate()
    {
        if (EventId == Guid.Empty || AccountId == Guid.Empty || TransactionId == Guid.Empty || CorrelationId == Guid.Empty)
        {
            throw new InvalidLedgerEventException("O envelope contém identificadores obrigatórios inválidos.");
        }

        if (EventVersion != LedgerEvent.CurrentEventVersion)
        {
            throw new InvalidLedgerEventException($"Versão de evento não suportada: {EventVersion}.");
        }

        if (Sequence <= 0 || Amount <= 0)
        {
            throw new InvalidLedgerEventException("O envelope contém sequência ou valor inválido.");
        }

        if (!string.Equals(Currency, AccountAggregate.SupportedCurrency, StringComparison.Ordinal))
        {
            throw new InvalidLedgerEventException($"Moeda de evento não suportada: {Currency}.");
        }

        if (string.IsNullOrEmpty(Description) || Description.Length > 255)
        {
            throw new InvalidLedgerEventException("A descrição do evento é obrigatória e deve ter até 255 caracteres.");
        }

        _ = GetMovementType();
    }
}
