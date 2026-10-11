# ADR 0011 - Blackjack: el primer juego de mesas

- **Estado:** aceptada
- **Fecha:** 2026-10-09

## Contexto
El ADR 0010 propuso un modelo de mesa comun para los juegos de cartas. Blackjack es el primero y el mas simple: mesas fijas contra el crupier, varios jugadores por mesa,
turnos, una carta tapada y apuestas que se resuelven contra la casa. Este ADR registra como se implemento y en que se separo (a proposito) de la propuesta.

## Decision
- **Modulo propio** (`Casino.Modules.Games.Blackjack`) que cumple el contrato (`IGameModule` + `IGameRounds`), con su motor en segundo plano: UN ciclo por mesa (las mesas
  corren en paralelo; dentro de una mesa todo es secuencial). Las mesas son fijas y salen de la configuracion (`Blackjack:Tables`; tres por defecto con topes distintos).
- **Sentarse es apostar.** La apuesta de cada jugador es una apuesta del protocolo de rondas de la Wallet (reservar -> resolver -> liquidar -> cerrar), con su `BetId`
  e `IdempotencyKey`. Asi no hizo falta tocar la Wallet: varios participantes son varias apuestas independientes que comparten una mano. (Las liquidaciones "que suman cero"
  del ADR 0010 no hacen falta contra el crupier: la casa es la contraparte; quedan para los juegos entre jugadores.)
- **Una mano por mesa, sin trabajo inutil.** La mano se abre y espera: **no corre ningun reloj ni se genera nada hasta la primera apuesta**, que arranca la ventana de
  apuestas. Si al cerrarla nadie llego a reservar, vuelve a esperar. (A diferencia de Crash, donde las rondas son continuas, una mesa vacia no cuesta nada.)
- **Documentos con concurrencia optimista, no un stream de eventos** (se aparta del ADR 0010): la mano y cada apuesta son documentos de Marten con `[Version]`, igual que
  Crash. El jugador que actua y el motor que da por terminado el turno compiten por la misma apuesta y gana el primero; lo mismo una reserva que llega justo al repartir
  (si la mano ya no acepta apuestas, se devuelve lo apostado). Un stream de eventos queda como mejora para cuando haya reconstruccion de partidas o repeticiones.
- **Carta tapada oculta de verdad.** La carta tapada del crupier no se guarda ni viaja: se calcula del zapato recien cuando se da vuelta. La semilla del zapato se guarda
  cifrada y se revela al terminar; su compromiso se publica antes de apostar. Las cartas de los jugadores son publicas en la mesa (como en un casino), pero los asientos
  no exponen quien es cada uno (ni su `userId`): solo el propio incluye su `BetId`.
- **Aviso en vivo minimo.** Reusa `GameBroadcast`: dice solo "la mesa X cambio" (sin cartas ni datos). El navegador vuelve a pedir el estado de la mesa, que arma el servidor.
- **Mazo provably fair:** zapato de 6 barajas barajado con Fisher-Yates y numeros uniformes de HMAC-SHA256 con rechazo (sin sesgo). Especificacion, orden de reparto y vectores
  de una implementacion independiente en `docs/juego-blackjack.md`; se verifica tambien en el navegador.
- **Reglas de esta primera version:** el crupier planta en todos los 17; blackjack natural paga 3 a 2 (siempre enteros); sin doblar, dividir ni seguro; si el crupier tiene
  blackjack se muestra al repartir y nadie juega; turno por asiento con tiempo limite (si no actua, se planta); la reserva de la Wallet dura hasta el final de la mano (`TtlSeconds`).

## Consecuencias
- Un solo motor de mesas (como el de Crash): `Blackjack:EngineEnabled` lo apaga en las demas instancias; no hay afinidad de mesas entre instancias todavia.
- Una caida del servicio corta las manos en curso y devuelve lo apostado.
- Falta, para versiones siguientes: doblar, dividir, seguro, mesas creadas por jugadores y asientos con varias manos. Truco, Uno y Poker (entre jugadores) reusan el modelo de
  mesa y suman liquidaciones entre participantes, mazo con aportes de cada uno y partidas que sobreviven a un reinicio.
