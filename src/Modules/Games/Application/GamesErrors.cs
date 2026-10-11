using Casino.Modules.Games.Application;

namespace Casino.Modules.Games.Application;

public enum GamesError
{
    InvalidBet,
    InvalidIdempotencyKey,
    BetKeyReused,
    RoundNotFound,
    InvalidSettings,
    SettingsConflict,

    /// <summary>No hay una ronda abierta para apostar (o ya se cerro el plazo).</summary>
    BettingClosed,

    /// <summary>La ronda no esta en el momento que la operacion necesita.</summary>
    RoundNotRunning,

    /// <summary>El cohete exploto antes de que el retiro llegara.</summary>
    CrashedAlready,

    /// <summary>La apuesta ya no esta en juego (ya retiro, ya perdio o nunca se reservo).</summary>
    BetNotActive,

    /// <summary>La mesa no existe (o el juego no la tiene configurada).</summary>
    TableNotFound,

    /// <summary>No es el turno de esa apuesta (o ya paso su tiempo).</summary>
    NotYourTurn,

    /// <summary>El jugador ya tiene una apuesta en esta mano de la mesa.</summary>
    AlreadySeated,

    /// <summary>La mesa no esta en el estado que la operacion necesita (ya empezo, termino, esta llena...).</summary>
    TableClosed,

    /// <summary>La jugada no es valida segun las reglas del juego.</summary>
    InvalidAction,
}

public sealed class GamesDomainException(GamesError error, string message) : Exception(message)
{
    public GamesError Error { get; } = error;
}

/// <summary>Estados de una ronda, comunes a todos los juegos que mueven fichas.</summary>
public enum RoundStatus
{
    /// <summary>La apuesta se coloco: nonce asignado y orden de reserva encolada.</summary>
    Placed = 1,

    /// <summary>La Wallet reservo las fichas y el juego ya sorteo. Falta que la Wallet liquide.</summary>
    Resolved = 2,

    /// <summary>La Wallet liquido el premio. Estado final.</summary>
    Settled = 3,

    /// <summary>La Wallet rechazo la reserva (por ejemplo, saldo insuficiente). Estado final: nunca se jugo.</summary>
    Rejected = 4,

    /// <summary>La ronda se anulo y las fichas volvieron al jugador (vencio la reserva o la liquidacion fue rechazada). Estado final.</summary>
    Voided = 5,
}

/// <summary>Resultado de colocar una apuesta. La resolucion es asincrona: se consulta la ronda por su BetId.</summary>
public sealed record PlacedBet(Guid BetId, RoundStatus Status, long Nonce, string Commitment, string ClientSeed, bool AlreadyPlaced);
