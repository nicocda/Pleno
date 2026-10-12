# ADR 0016 - Renovacion de reservas de la Wallet

- **Estado:** aceptada
- **Fecha:** 2026-10-13

## Contexto
Cuando un juego reserva las fichas de una apuesta, la Wallet programa un mensaje `ExpireReservation` que la libera sola si nadie la liquida: es la red de seguridad contra una ronda o una mesa que
quedo colgada. El plazo esta acotado a una hora (`ReserveStakeHandler.MaxTtlSeconds`). Eso alcanzaba mientras las partidas eran cortas. Un **torneo de poker** puede durar mas de una hora desde que
los jugadores se sientan, y la **mesa libre** (ADR 0015) no tiene fin: si la reserva vence con la partida en marcha, la Wallet devuelve las fichas al jugador, la mesa anula su apuesta y el premio ya no
se puede pagar (o quedan fichas duplicadas entre la pila de la mesa y el saldo).

Quitar el vencimiento no sirve: una reserva huerfana (el servicio de juegos se cayo, una mesa quedo sin motor) bloquearia las fichas para siempre.

## Decision
- **El vencimiento pasa a ser renovable.** Nueva orden `ExtendReservation(BetId, AccountId, TtlSeconds)`: pide que la reserva siga abierta `TtlSeconds` mas desde ahora (acotado igual que al reservar). Renovar una
  reserva que ya se cerro, o que no existe, no hace nada.
- **Como lo hace la Wallet, sin tocar el libro de eventos:** renovar no mueve fichas, asi que no es un evento del ledger. Se guarda un documento `ReservationLease` (id de la reserva y hasta cuando vale) y se
  programa **otro** `ExpireReservation` para ese momento. Cuando llega un vencimiento, `ExpireReservationAsync` mira el documento: si todavia vale mas adelante, lo ignora (hay otro programado); si no, libera la reserva
  como siempre y limpia el documento. Nunca se acorta un plazo ya mas largo. Sin documento, vale el vencimiento original: las reservas que nadie renueva se comportan exactamente como antes.
- **Quien renueva:** el motor de mesas (`Tables:ReservationRenewSeconds`, por defecto 600 s) renueva las reservas de cada partida **en curso**. Las mesas que todavia esperan jugadores no hacen falta (esperan menos que el
  plazo) y las terminadas ya liquidaron. Si el motor se cae, deja de renovar y la reserva vence a la hora desde la ultima renovacion: la red de seguridad se conserva.

## Consecuencias
- Una partida puede durar mas de una hora sin perder las fichas reservadas; una reserva huerfana se libera sola como maximo una hora despues de la ultima renovacion.
- El costo es un mensaje por asiento cada 10 minutos mientras se juega, y un documento chico por reserva renovada (se borra cuando llega el ultimo vencimiento).
- No cubre una mesa que sigue "viva" con el motor prendido pero sin nadie jugando (todos ausentes para siempre): cerrar esas mesas es una regla de las mesas libres (ADR 0015, fase 3), no de la Wallet.
