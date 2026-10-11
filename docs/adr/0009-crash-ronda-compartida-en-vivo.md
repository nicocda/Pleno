# ADR 0009 - Crash: un juego de ronda compartida en vivo

- **Estado:** aceptada
- **Fecha:** 2026-10-09

## Contexto
La ruleta y la tragamonedas son de una sola tirada por jugador: se apuesta, se sortea con la semilla de ese jugador y se cobra. Crash es distinto: una ronda
**compartida** por todos que dura decenas de segundos, con decisiones del jugador mientras corre (retirar) y un resultado que no puede depender de la semilla de
ningun jugador. Es la prueba de que el contrato de juegos (ADR 0008) alcanza para juegos muy distintos.

## Decision
- **Modulo propio** (`Casino.Modules.Games.Crash`) que cumple el contrato (`IGameModule` + `IGameRounds`). Trae su propio **motor en segundo plano** (`CrashEngine`)
  que abre la ronda, deja subir el multiplicador, hace los retiros automaticos en su instante exacto y explota. Corre en UNA sola instancia del servicio de juegos.
- **Provably fair por ronda, no por jugador:** la semilla es de la ronda; se publica su hash (compromiso) ANTES de aceptar apuestas y se revela al explotar. El punto de
  explosion sale de HMAC-SHA256 con enteros (sin decimales); la semilla se guarda cifrada hasta revelarla. Especificacion y vectores en `docs/juego-crash.md`; se verifica
  tambien en el navegador.
- **El servidor decide el multiplicador por su reloj.** Los tiempos salen de la hora de inicio y de la formula `e^(0,07 t)`, no de ticks; el cliente solo dibuja.
- **Concurrencia:** el retiro del jugador y la explosion del motor compiten por la misma apuesta; se resuelve con concurrencia optimista (gana el primero) y cada apuesta se
  resuelve en su propia transaccion.
- **La reserva de la Wallet tiene plazo propio.** `ReserveStake` gana un `TtlSeconds` opcional (acotado a 1 a 3.600 s desde el ADR 0012; al principio era 900): una ronda de Crash puede durar ~100 s y el plazo por
  defecto (60 s) liberaria la apuesta en medio del juego. Los demas juegos no cambian.
- **Difusion en vivo a todos:** un mensaje generico `GameBroadcast` (el juego lo publica por RabbitMQ; el tiempo real lo reenvia por SignalR al grupo de todos los
  jugadores). La plataforma solo reenvia el JSON del juego, no lo interpreta; sirve para cualquier juego en vivo. El navegador dibuja con la hora del servidor y consulta el
  estado como respaldo.
- **Nadie pierde por una caida:** una apuesta que llega tarde, o una ronda que se corta por un reinicio, devuelve lo apostado.

## Consecuencias
- Un solo motor de rondas: no hay eleccion de lider entre instancias (queda para cuando haya mas de una); `Crash:EngineEnabled` lo apaga en las demas.
- Las rondas y apuestas viven en `gamesdb`; las pruebas corren una ronda a la vez con semillas elegidas para saber donde explota.
- Una caida del servicio de juegos corta la ronda en curso (se devuelve lo apostado) y el motor retoma con una ronda nueva al volver.
