namespace Casino.Modules.Wallet.Domain;

public enum LedgerKind
{
    Credit = 1,
    Reserve = 2,
    Settle = 3,
    Release = 4,
    Reversal = 5,
    Transfer = 6,
    PayOut = 7,
}

/// <summary>Lo minimo que el agregado necesita recordar de una transaccion para poder revertirla.</summary>
public sealed record LedgerRecord(
    Guid TransactionId,
    LedgerKind Kind,
    IReadOnlyList<Entry> Entries,
    Guid? ReservationId,
    long Stake);

public sealed record ProcessedKey(Guid TransactionId, string Fingerprint);

/// <summary>
/// Foto del estado de una cuenta en una version del stream. Cargar = snapshot + eventos posteriores,
/// en vez de reproducir toda la historia. Los eventos siguen siendo la fuente de verdad.
/// </summary>
public sealed class WalletSnapshot
{
    /// <summary>Id de la cuenta (tambien es el id del stream).</summary>
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public long Available { get; set; }

    public long Reserved { get; set; }

    public long Version { get; set; }

    public Dictionary<string, ProcessedKey> ProcessedKeys { get; set; } = [];

    public List<LedgerRecord> Ledger { get; set; } = [];

    public Dictionary<Guid, long> OpenReservations { get; set; } = [];

    public List<Guid> KnownReservations { get; set; } = [];

    public List<Guid> Reversed { get; set; } = [];
}
