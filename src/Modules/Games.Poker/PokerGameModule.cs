using Casino.Modules.Games.Tables;

namespace Casino.Modules.Games.Poker;

/// <summary>
/// Poker (Texas Hold'em, una mano por mesa) como modulo de la plataforma de juegos: un juego de mesas ENTRE JUGADORES. Todo lo que no son las reglas (mesas, bots, turnos,
/// fichas, semilla, tiempo real) lo pone <see cref="TableGameModule"/> (ADR 0012); el modulo solo aporta <see cref="PokerGame"/>.
/// </summary>
public sealed class PokerGameModule() : TableGameModule(new PokerGame());
