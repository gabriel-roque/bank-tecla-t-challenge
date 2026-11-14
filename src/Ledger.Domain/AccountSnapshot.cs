using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ledger.Domain;

public sealed record AccountSnapshot(
    Guid AccountId,
    long Sequence,
    long BalanceMinor,
    string Currency,
    short SchemaVersion,
    string Checksum,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastRecordedAt)
{
    public const short CurrentSchemaVersion = 1;

    public static AccountSnapshot Create(
        Guid accountId,
        long sequence,
        long balanceMinor,
        DateTimeOffset createdAt,
        DateTimeOffset lastRecordedAt)
    {
        // PostgreSQL stores timestamptz with microsecond precision while .NET timestamps
        // have 100-nanosecond ticks. Normalize before signing so the checksum survives a
        // database round-trip.
        DateTimeOffset normalizedLastRecordedAt = NormalizeToPostgresPrecision(lastRecordedAt);
        string checksum = ComputeChecksum(
            accountId,
            sequence,
            balanceMinor,
            AccountAggregate.SupportedCurrency,
            CurrentSchemaVersion,
            normalizedLastRecordedAt);

        return new AccountSnapshot(
            accountId,
            sequence,
            balanceMinor,
            AccountAggregate.SupportedCurrency,
            CurrentSchemaVersion,
            checksum,
            createdAt,
            normalizedLastRecordedAt);
    }

    public void ValidateIntegrity()
    {
        if (AccountId == Guid.Empty || Sequence <= 0 || BalanceMinor < 0)
        {
            throw new InvalidSnapshotException("O snapshot contém estado inválido.");
        }

        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidSnapshotException($"Versão de snapshot não suportada: {SchemaVersion}.");
        }

        if (!string.Equals(Currency, AccountAggregate.SupportedCurrency, StringComparison.Ordinal))
        {
            throw new InvalidSnapshotException($"Moeda de snapshot não suportada: {Currency}.");
        }

        string expected = ComputeChecksum(
            AccountId,
            Sequence,
            BalanceMinor,
            Currency,
            SchemaVersion,
            LastRecordedAt);

        byte[] actualBytes;
        try
        {
            actualBytes = Convert.FromHexString(Checksum);
        }
        catch (FormatException exception)
        {
            throw new InvalidSnapshotException("O checksum do snapshot possui formato inválido.", exception);
        }

        byte[] expectedBytes = Convert.FromHexString(expected);
        if (CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes))
        {
            return;
        }

        // Snapshots created before timestamps were normalized were signed with the
        // sub-microsecond tick that PostgreSQL subsequently discarded. Try the ten
        // possible tick values so existing data remains readable during the upgrade.
        for (long subMicrosecondTick = 0; subMicrosecondTick < TimeSpan.TicksPerMicrosecond; subMicrosecondTick++)
        {
            string legacy = ComputeChecksumExact(
                AccountId,
                Sequence,
                BalanceMinor,
                Currency,
                SchemaVersion,
                LastRecordedAt.AddTicks(subMicrosecondTick));
            byte[] legacyBytes = Convert.FromHexString(legacy);
            if (CryptographicOperations.FixedTimeEquals(actualBytes, legacyBytes))
            {
                return;
            }
        }

        throw new InvalidSnapshotException("O checksum do snapshot não corresponde ao estado armazenado.");
    }

    private static DateTimeOffset NormalizeToPostgresPrecision(DateTimeOffset value)
    {
        long remainder = value.Ticks % TimeSpan.TicksPerMicrosecond;
        return value.AddTicks(-remainder);
    }

    private static string ComputeChecksum(
        Guid accountId,
        long sequence,
        long balanceMinor,
        string currency,
        short schemaVersion,
        DateTimeOffset lastRecordedAt) =>
        ComputeChecksumExact(
            accountId,
            sequence,
            balanceMinor,
            currency,
            schemaVersion,
            NormalizeToPostgresPrecision(lastRecordedAt));

    private static string ComputeChecksumExact(
        Guid accountId,
        long sequence,
        long balanceMinor,
        string currency,
        short schemaVersion,
        DateTimeOffset lastRecordedAt)
    {
        string source = string.Create(
            CultureInfo.InvariantCulture,
            $"{accountId:N}|{sequence}|{balanceMinor}|{currency}|{schemaVersion}|{lastRecordedAt:O}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }
}
