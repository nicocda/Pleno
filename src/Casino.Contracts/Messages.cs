namespace Casino.Contracts;

// Contratos de mensajeria entre modulos: el unico "idioma" que comparten. Sin logica ni dependencias.
// Convencion: las ordenes (comandos) estan en imperativo; los hechos (eventos), en pasado.
// BetId identifica la apuesta de punta a punta y es tambien el id de la reserva en la Wallet.

// ---- Ordenes hacia la Wallet ----

/// <summary>
/// Un juego pide reservar las fichas de una apuesta. <paramref name="TtlSeconds"/> es cuanto puede quedar abierta la reserva antes de liberarse
/// sola: la mayoria de los juegos resuelve en segundos y usa el plazo por defecto de la Wallet; uno de ronda larga (Crash) pide un plazo mayor.
/// </summary>
public sealed record ReserveStake(Guid BetId, Guid AccountId, long Stake, int? TtlSeconds = null, string? GameId = null);

/// <summary>
/// Un juego en vivo difunde un hecho a TODOS los jugadores conectados (por ejemplo, "se abrio la ronda" o "exploto el cohete").
/// <paramref name="Data"/> es JSON propio del juego: la plataforma solo lo reenvia, no lo interpreta.
/// </summary>
public sealed record GameBroadcast(string Game, string Kind, string Data, DateTimeOffset At);

/// <summary>Un juego informa que la ronda se resolvio. La Wallet liquida con el premio TOTAL (0 si perdio).</summary>
public sealed record RoundResolved(Guid BetId, Guid AccountId, long Payout);

// ---- Hechos que publica la Wallet ----

public sealed record StakeReserved(Guid BetId, Guid AccountId, long Stake, Guid TransactionId);

public sealed record StakeRejected(Guid BetId, Guid AccountId, string Reason);

public sealed record StakeSettled(Guid BetId, Guid AccountId, long Stake, long Payout, Guid TransactionId);

public sealed record StakeReleased(Guid BetId, Guid AccountId, long Stake, Guid TransactionId);

/// <summary>La liquidacion no se pudo aplicar (por ejemplo, la reserva ya habia vencido). El juego debe anular la ronda.</summary>
public sealed record StakeSettlementRejected(Guid BetId, Guid AccountId, string Reason);

/// <summary>Cambio de saldo de una cuenta, para quien necesite mostrarlo en vivo.</summary>
public sealed record BalanceChanged(Guid AccountId, long Available, long Reserved, long Version);

/// <summary>Mensaje programado por la Wallet para si misma: si la reserva sigue abierta al vencer, se libera.</summary>
public sealed record ExpireReservation(Guid BetId, Guid AccountId);

// ---- Usuarios ----

/// <summary>Un jugador entro por primera vez. La Wallet abre su cuenta (el id se deriva del usuario) y le acredita las fichas de bienvenida.</summary>
public sealed record UserRegistered(Guid UserId);

// ---- Hechos que publican los juegos ----

/// <summary>
/// Una ronda termino (cobrada, rechazada o anulada). Lo consume el tiempo real para avisar al jugador.
/// <paramref name="Status"/> es el nombre del estado final: Settled, Rejected o Voided.
/// </summary>
public sealed record RoundClosed(
    Guid BetId,
    Guid AccountId,
    string Game,
    string Status,
    int? WinningNumber,
    long Stake,
    long Payout,
    string? FailureReason);
