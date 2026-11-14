namespace Ledger.Domain;

public sealed class InsufficientFundsException(long balanceMinor, long requestedMinor)
    : Exception($"Saldo insuficiente. Saldo: {balanceMinor}; débito solicitado: {requestedMinor}.")
{
    public long BalanceMinor { get; } = balanceMinor;

    public long RequestedMinor { get; } = requestedMinor;
}

public sealed class InvalidMovementException(string message) : Exception(message);

public sealed class InvalidSnapshotException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class InvalidLedgerEventException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class EventSequenceException(long expected, long actual)
    : Exception($"Sequência inválida. Esperada: {expected}; recebida: {actual}.")
{
    public long Expected { get; } = expected;

    public long Actual { get; } = actual;
}
