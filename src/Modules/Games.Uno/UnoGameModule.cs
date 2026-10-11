using Casino.Modules.Games.Tables;

namespace Casino.Modules.Games.Uno;

/// <summary>
/// Uno como modulo de la plataforma de juegos: un juego de mesas ENTRE JUGADORES. Todo lo que no son las reglas (mesas, bots, turnos, fichas, semilla, tiempo real)
/// lo pone <see cref="TableGameModule"/> (ADR 0012); el modulo solo aporta <see cref="UnoGame"/>.
/// </summary>
public sealed class UnoGameModule() : TableGameModule(new UnoGame());
