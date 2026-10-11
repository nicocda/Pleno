using Casino.Modules.Games.Tables;

namespace Casino.Modules.Games.Truco;

/// <summary>
/// Truco como modulo de la plataforma de juegos: un juego de mesas ENTRE JUGADORES (dos). Todo lo que no son las reglas (mesas, bots, turnos, fichas, semilla,
/// tiempo real) lo pone <see cref="TableGameModule"/> (ADR 0012); el modulo solo aporta <see cref="TrucoGame"/>.
/// </summary>
public sealed class TrucoGameModule() : TableGameModule(new TrucoGame());
