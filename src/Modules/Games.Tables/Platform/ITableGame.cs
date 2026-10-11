using Casino.Modules.Games.Platform;

namespace Casino.Modules.Games.Tables;

/// <summary>Una jugada que las reglas rechazan (no es tu carta, no podes cantar eso ahora...). El mensaje se le muestra al jugador.</summary>
public sealed class TableRuleException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Lo que el juego necesita para armar una partida nueva.</summary>
/// <param name="Seats">Cuantos asientos juegan (humanos y bots).</param>
/// <param name="Seed">La semilla de la partida (provably fair): el mazo sale de ella.</param>
/// <param name="TableId">Id de la mesa (se usa para separar los flujos de numeros de cada partida).</param>
/// <param name="BuyIn">Lo que puso cada asiento.</param>
public sealed record GameStartContext(int Seats, string Seed, Guid TableId, long BuyIn, DateTimeOffset Now);

/// <summary>A quien le toca y cuanto tiene para decidir.</summary>
public sealed record TurnInfo(int Seat, double Seconds);

/// <summary>El resultado final: lo que se lleva cada asiento (en fichas enteras). La suma tiene que ser igual a lo que se puso entre todos.</summary>
public sealed record GameOutcome(IReadOnlyList<long> Payouts);

/// <summary>
/// El contrato de un juego de mesas ENTRE JUGADORES (Uno, Truco, Poker...). Es una maquina de estados PURA: sin base de datos, sin reloj propio, sin saber de la Wallet
/// ni de quien es cada jugador (solo conoce asientos 0..n-1). La plataforma (<see cref="TableService"/>) se ocupa de las mesas, las fichas, los turnos, los bots, los
/// tiempos, la semilla y el tiempo real; el juego solo decide las reglas. El estado viaja como JSON (lo guarda la plataforma): asi los juegos se prueban sin infraestructura.
/// </summary>
public interface ITableGame
{
    GameInfo Info { get; }

    int MinPlayers { get; }

    int MaxPlayers { get; }

    long MinBuyIn { get; }

    long MaxBuyIn { get; }

    /// <summary>Arma la partida (reparte, baraja con la semilla...) y devuelve el estado inicial.</summary>
    string Start(GameStartContext context);

    /// <summary>De quien es el turno, o null si la partida termino.</summary>
    TurnInfo? Turn(string state);

    /// <summary>Aplica la jugada de un asiento y devuelve el estado nuevo. Si las reglas no la permiten lanza <see cref="TableRuleException"/>.</summary>
    string Apply(string state, int seat, string action, DateTimeOffset now);

    /// <summary>La jugada que se hace por un jugador que no actuo a tiempo.</summary>
    string TimeoutAction(string state, int seat);

    /// <summary>La jugada de un bot (o de un jugador ausente) en ese asiento. Siempre tiene que ser legal.</summary>
    string BotAction(string state, int seat);

    /// <summary>Lo que ve ese asiento (<c>null</c> = un espectador): solo lo suyo, nunca las cartas ocultas de otros ni el mazo.</summary>
    object View(string state, int? seat);

    /// <summary>El resultado final, o null si la partida sigue.</summary>
    GameOutcome? Outcome(string state);
}
