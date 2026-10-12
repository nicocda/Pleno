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
/// <param name="Options">Las opciones de la mesa ya normalizadas por <see cref="ITableGame.Quote"/> (JSON propio del juego), o null si el juego no tiene.</param>
public sealed record GameStartContext(int Seats, string Seed, Guid TableId, long BuyIn, DateTimeOffset Now, string? Options = null);

/// <summary>
/// A quien le toca y cuanto tiene para decidir. <paramref name="Automatic"/> marca una pausa del propio juego (por ejemplo entre dos manos de poker): cuando vence, el motor juega
/// <see cref="ITableGame.TimeoutAction"/> sin contarle una falta al asiento ni dejar que "juegue un bot".
/// </summary>
public sealed record TurnInfo(int Seat, double Seconds, bool Automatic = false);

/// <summary>
/// El resultado final: lo que se lleva cada asiento (en fichas enteras). Los pagos mas lo que se queda la casa (<paramref name="HouseTake"/>, por ejemplo la comision de un torneo)
/// tienen que ser iguales a lo que se reservo entre todos: la plataforma lo verifica antes de pagar.
/// </summary>
public sealed record GameOutcome(IReadOnlyList<long> Payouts, long HouseTake = 0);

/// <summary>Una opcion con la que se crea una mesa de un juego (para que la pantalla arme el formulario). <c>Kind</c>: <c>number</c> (campo numerico) o <c>choice</c> (una de <c>Choices</c>).</summary>
public sealed record TableOption(string Key, string Label, string Hint, string Kind, IReadOnlyList<long>? Choices = null, long? Min = null, long? Max = null, long? Default = null);

/// <summary>
/// La cotizacion de una mesa antes de crearla: cuanto se reserva por asiento (entrada, comision, recompras posibles...), las opciones ya normalizadas, un resumen corto para el listado y el
/// desglose para mostrarle al jugador. Si lo que se pidio no es valido el juego lanza <see cref="TableRuleException"/> con el motivo.
/// </summary>
public sealed record TableQuote(long Stake, string? OptionsJson, string? Description, string? Breakdown, bool Continuous = false, long MinEntry = 0, long MaxEntry = 0);

/// <summary>Lo que un asiento se lleva de una mesa continua al levantarse (o al cerrarse la mesa): su pila, en fichas enteras (puede ser 0). La plataforma lo liquida contra las reservas de ese asiento.</summary>
public sealed record CashOut(int Seat, long Chips);

/// <summary>Un juego que lee su propia configuracion (seccion del appsettings) al registrarse: por ejemplo la comision de la casa.</summary>
public interface IConfigurableGame
{
    void Configure(Microsoft.Extensions.Configuration.IConfiguration configuration);
}

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

    /// <summary>Si el dueño puede sumar bots a la mesa. Un juego donde la casa no quiere poner fichas (poker) lo apaga: quien se ausenta no apuesta, pasa o se retira.</summary>
    bool AllowsBots => true;

    /// <summary>Las opciones de creacion propias del juego (ciegas, recompras...). Vacio si no tiene.</summary>
    IReadOnlyList<TableOption> Options => [];

    /// <summary>
    /// Valida la mesa que se quiere crear y la cotiza (cuanto se reserva por asiento y las opciones normalizadas). Por defecto la entrada es lo unico que se reserva. Lanza
    /// <see cref="TableRuleException"/> si algo no es valido.
    /// </summary>
    TableQuote Quote(long buyIn, int maxPlayers, System.Text.Json.JsonElement? options) => new(buyIn, null, null, null);

    // ---- Mesas continuas (la mesa libre del Poker): no terminan; se entra, se recarga y se sale cuando se quiere ----

    /// <summary>Se sienta a alguien con <paramref name="chips"/> en un asiento libre (sus fichas ya estan reservadas). Si con eso hay quien juegue, arranca la mano. Lanza <see cref="TableRuleException"/> si no se puede.</summary>
    string SeatPlayer(string state, int seat, long chips, DateTimeOffset now) => throw new NotSupportedException("Este juego no tiene mesas continuas.");

    /// <summary>Suma fichas ya reservadas a quien esta sentado (una recarga). Lanza <see cref="TableRuleException"/> si no se puede (pila maxima, asiento vacio...): la plataforma devuelve entonces lo reservado.</summary>
    string AddChips(string state, int seat, long chips, DateTimeOffset now) => throw new NotSupportedException("Este juego no tiene mesas continuas.");

    /// <summary>Saca del estado a quienes se levantaron y devuelve lo que se llevan, para que la plataforma los liquide y libere el asiento.</summary>
    (string State, IReadOnlyList<CashOut> CashOuts) DrainCashOuts(string state) => (state, []);

    /// <summary>Cierra la mesa: todos los sentados se levantan con lo que tienen (y lo que pusieron en la mano en curso vuelve a su dueño).</summary>
    string Shutdown(string state) => state;

    /// <summary>Si ese asiento juega "en automatico" (se esta levantando): el motor juega por el enseguida en vez de esperar su turno.</summary>
    bool IsPassive(string state, int seat) => false;

    /// <summary>Si ese asiento puede jugar ahora. Por defecto, solo el del turno; un juego puede dejar jugar fuera de turno (por ejemplo, recomprar entre manos).</summary>
    bool MayAct(string state, int seat) => Turn(state) is { } turn && turn.Seat == seat;

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
