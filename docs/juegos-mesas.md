# Juegos de mesas entre jugadores

La plataforma `Casino.Modules.Games.Tables` ([ADR 0012](adr/0012-plataforma-de-mesas-entre-jugadores.md)) pone todo lo que comparten Uno, Truco y Poker; cada juego solo escribe
sus reglas. Complementa al [contrato de juegos](juegos-contrato.md) (y a las mesas fijas contra la casa de [Blackjack](juego-blackjack.md)).

## Lo que pone la plataforma
- **Mesas:** crear (entrada, jugadores, pública o privada con código), entrar, salir, agregar/quitar bots, iniciar, listar (`GET /games/{id}/tables`) y ver la mesa desde *tu* asiento.
- **Fichas:** cada asiento humano es una apuesta del protocolo de rondas con la Wallet (reserva al sentarse, liquida con lo que se llevó); salir antes de empezar, cancelar o una reserva
  tardía devuelven lo apostado. Los pagos del juego tienen que sumar lo puesto entre todos (bots incluidos).
- **Turnos y tiempos:** el turno tiene un límite (lo define el juego); si vence, juega el turno de "no actuó"; con 3 vencidos seguidos el asiento queda ausente y juega un bot.
- **Bots:** el motor los juega con la jugada que da el juego (`BotAction`); no reservan fichas (la casa los respalda).
- **Provably fair:** semilla al crear la mesa, compromiso público desde el principio, revelada al terminar; `FairShuffle` baraja con HMAC-SHA256.
- **Tiempo real:** `GameBroadcast` `tableChanged` (sin datos); el navegador vuelve a pedir su vista. Nadie ve la identidad de otro (solo "Jugador N" / "Bot N").
- **Endpoints** comunes bajo `/games/{id}`: `rules`, `tables` (GET/POST), `tables/{id}` (GET), `join`, `leave`, `bots` (POST/DELETE), `start`, `action`, `history`.

## Cómo se agrega un juego
1. Un proyecto `Casino.Modules.Games.X` que referencia `Games.Tables` y una clase que implementa **`ITableGame`** (máquina de estados pura, el estado es un JSON):
   `Start` (reparte con `FairShuffle` y la semilla de la mesa), `Turn` (a quién le toca y cuánto tiene), `Apply` (una jugada; lanza `TableRuleException` si es ilegal),
   `TimeoutAction`, `BotAction`, `View` (lo que ve ese asiento, **nunca** lo oculto de los demás) y `Outcome` (lo que se lleva cada asiento; null si sigue).
2. `public sealed class XGameModule() : TableGameModule(new XGame());` y agregarlo a `AvailableGames.All()`.
3. Tests unitarios con partidas completas de bots contra bots (cada paso legal, las cartas se conservan, el pago suma lo puesto) y la página del front (que reutiliza `TablesLobby`).
La plataforma ya trae sus tests de integración (mesas, fichas, ausentes, privadas, cancelación) con Uno como juego.

## Juegos
- [Uno](juego-uno.md) · [Truco](juego-truco.md) · [Poker](juego-poker.md).

## Configuración (sección `Tables`)
`BotThinkMilliseconds` (1.500), `OpenTableMinutes` (15), `ReservationSeconds` (3.600), `TickMilliseconds` (300) y `EngineEnabled` (true). **El motor tiene que correr en UNA sola
instancia** del servicio de juegos; las pruebas lo apagan y lo manejan a mano (`TableService.TickAsync`).
