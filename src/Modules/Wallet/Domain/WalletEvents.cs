namespace Casino.Modules.Wallet.Domain;

public abstract record WalletEvent(DateTimeOffset OccurredAt);

public sealed record AccountOpened(Guid AccountId, Guid UserId, DateTimeOffset OccurredAt)
    : WalletEvent(OccurredAt);

/// <summary>
/// Evento base de toda operacion que mueve fichas. Un evento por operacion, con todos sus asientos.
/// <paramref name="Fingerprint"/> resume el contenido de la peticion: reusar una IdempotencyKey con otro contenido se rechaza.
/// </summary>
public abstract record LedgerEvent(
    Guid TransactionId,
    string IdempotencyKey,
    string Fingerprint,
    IReadOnlyList<Entry> Entries,
    DateTimeOffset OccurredAt) : WalletEvent(OccurredAt);

public sealed record ChipsCredited(
    Guid TransactionId,
    string IdempotencyKey,
    string Fingerprint,
    IReadOnlyList<Entry> Entries,
    DateTimeOffset OccurredAt,
    long Amount) : LedgerEvent(TransactionId, IdempotencyKey, Fingerprint, Entries, OccurredAt);

/// <summary>
/// Transferencia de fichas entre dos jugadores (cargas de cajeros, cobros). Se guarda UNA vez en cada cuenta (la que sale y la que recibe) con los MISMOS
/// asientos y el MISMO <c>TransactionId</c>; cada cuenta solo proyecta su parte. Las dos mitades se escriben en una sola transaccion.
/// </summary>
public sealed record ChipsTransferred(
    Guid TransactionId,
    string IdempotencyKey,
    string Fingerprint,
    IReadOnlyList<Entry> Entries,
    DateTimeOffset OccurredAt,
    Guid FromUserId,
    Guid ToUserId,
    long Amount) : LedgerEvent(TransactionId, IdempotencyKey, Fingerprint, Entries, OccurredAt);

/// <summary>
/// Una reserva abierta que se paga a otro jugador en vez de volver o quedar para la casa (el retiro: el jugador pone fichas en reserva y su cajero las cobra). Cierra la reserva; el que recibe lo
/// registra como <see cref="ChipsTransferred"/> en su cuenta, en la misma transaccion.
/// </summary>
public sealed record ReservationPaidOut(
    Guid TransactionId,
    string IdempotencyKey,
    string Fingerprint,
    IReadOnlyList<Entry> Entries,
    DateTimeOffset OccurredAt,
    Guid ReservationId,
    long Stake,
    Guid ToUserId) : LedgerEvent(TransactionId, IdempotencyKey, Fingerprint, Entries, OccurredAt);

public sealed record BetReserved(
    Guid TransactionId,
    string IdempotencyKey,
    string Fingerprint,
    IReadOnlyList<Entry> Entries,
    DateTimeOffset OccurredAt,
    Guid ReservationId,
    long Stake,
    string? GameId = null) : LedgerEvent(TransactionId, IdempotencyKey, Fingerprint, Entries, OccurredAt);

public sealed record BetSettled(
    Guid TransactionId,
    string IdempotencyKey,
    string Fingerprint,
    IReadOnlyList<Entry> Entries,
    DateTimeOffset OccurredAt,
    Guid ReservationId,
    long Stake,
    long Payout) : LedgerEvent(TransactionId, IdempotencyKey, Fingerprint, Entries, OccurredAt);

public sealed record ReservationReleased(
    Guid TransactionId,
    string IdempotencyKey,
    string Fingerprint,
    IReadOnlyList<Entry> Entries,
    DateTimeOffset OccurredAt,
    Guid ReservationId,
    long Stake) : LedgerEvent(TransactionId, IdempotencyKey, Fingerprint, Entries, OccurredAt);

public sealed record OperationReversed(
    Guid TransactionId,
    string IdempotencyKey,
    string Fingerprint,
    IReadOnlyList<Entry> Entries,
    DateTimeOffset OccurredAt,
    Guid ReversedTransactionId) : LedgerEvent(TransactionId, IdempotencyKey, Fingerprint, Entries, OccurredAt);
