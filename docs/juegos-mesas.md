# Juegos de mesas entre jugadores

La plataforma `Casino.Modules.Games.Tables` ([ADR 0012](adr/0012-plataforma-de-mesas-entre-jugadores.md)) pone todo lo que comparten Uno, Truco y Poker; cada juego solo escribe
sus reglas. Complementa al [contrato de juegos](juegos-contrato.md) (y a las mesas fijas contra la casa de [Blackjack](juego-blackjack.md)).

## Lo que pone la plataforma
- **Mesas:** crear (entrada, jugadores, pública o privada con código), entrar, salir, agregar/quitar bots, iniciar, listar (`GET /games/{id}/tables`) y ver la mesa desde *tu* asiento.
- **Fichas:** cada asiento humano es una apuesta del protocolo de rondas con la Wallet (reserva al sentarse, liquida con lo que se llevó); salir antes de empezar, cancelar o una reserva
  tardía devuelven lo apostado. Los pagos del juego más lo que se queda la casa (`GameOutcome.HouseTake`, por ejemplo la comisión de un torneo) tienen que sumar lo reservado entre todos (bots incluidos).
  Lo que se **reserva por asiento** (`PlayerTable.Stake`) puede ser más que la entrada: lo decide el juego al cotizar la mesa.
- **Opciones de cada juego y cotización:** un juego puede declarar opciones de creación (`ITableGame.Options`: números o elecciones, p. ej. ciegas y recompras del Poker) y cotizar la mesa (`ITableGame.Quote`: cuánto se
  reserva por asiento, las opciones ya normalizadas, un resumen y un desglose, o el motivo si no es válida). `POST /games/{id}/quote` la devuelve antes de crear; `GET /games/{id}/rules` lista las opciones y
  `CreateTableBody.Options` las trae. Un juego que lee configuración de la casa implementa `IConfigurableGame`.
- **Renovación de reservas:** la Wallet libera sola una reserva a la hora como máximo. Mientras una partida está en curso, el motor renueva la reserva de cada asiento cada `ReservationRenewSeconds`
  (`ExtendReservation`), así una partida larga no pierde sus fichas; si el motor se cae, deja de renovar y la reserva vence sola ([ADR 0016](adr/0016-renovacion-de-reservas.md)).
- **Mesas continuas (la mesa libre del Poker):** una mesa puede **no terminar** (`TableQuote.Continuous`). Nace `Playing` con todos los asientos (`TableSeat.IsVacant()` los libres) y el juego esperando
  jugadores; cada uno se sienta con su monto (`POST /games/{id}/tables/{id}/join` con `amount`, dentro de `MinEntry`..`MaxEntry`) y puede recargar (`.../topup`, una `TableBet` de tipo `TopUp` por recarga). Una reserva
  confirmada por la Wallet entra al juego con `ITableGame.SeatPlayer` o `AddChips` (si el juego no la acepta, se devuelve). El juego anota a quienes se levantan (`DrainCashOuts`) y la plataforma **liquida cada asiento
  por separado**: el pago va a la primera reserva del jugador (las recargas, que ya estan en la pila, quedan en 0), el asiento queda libre y, cuando se va el ultimo, la mesa termina. `IsPassive` deja que un asiento que se esta
  levantando juegue en automatico; `Shutdown` cierra la mesa devolviendo a cada uno lo suyo. Una mesa esperando jugadores (o con todos ausentes) mas de `Tables:CashIdleMinutes` (15) se cierra sola.
- **Jugar fuera de turno:** `ITableGame.MayAct(state, seat)` deja que un asiento actúe sin ser el del turno (por ejemplo, decidir una recompra durante la pausa).
- **Turnos y tiempos:** el turno tiene un límite (lo define el juego); si vence, juega el turno de "no actuó"; con 3 vencidos seguidos el asiento queda ausente y juega un bot.
  Un juego puede marcar un turno como **automático** (`TurnInfo.Automatic`, por ejemplo la pausa entre manos del Poker): al vencer el motor juega `TimeoutAction` sin contarle una falta a nadie.
- **Bots:** el motor los juega con la jugada que da el juego (`BotAction`); no reservan fichas (la casa los respalda). Un juego puede **no tenerlos** (`ITableGame.AllowsBots`, el Poker): la
  API rechaza sumarlos, `GET /games/{id}/rules` lo informa (`allowsBots`) y `BotAction` solo se usa para un asiento ausente.
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
`BotThinkMilliseconds` (1.500), `OpenTableMinutes` (15), `ReservationSeconds` (3.600), `ReservationRenewSeconds` (600), `CashIdleMinutes` (15), `TickMilliseconds` (300) y `EngineEnabled` (true). **El motor tiene que correr en UNA sola
instancia** del servicio de juegos; las pruebas lo apagan y lo manejan a mano (`TableService.TickAsync`).
