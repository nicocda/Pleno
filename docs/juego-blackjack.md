# Blackjack

Un juego de **mesas** contra el crupier, con varios jugadores por mesa. Llegá a 21 sin pasarte y por encima de la mano del crupier. Decisiones y motivos:
[ADR 0010](adr/0010-juegos-de-mesa-modelo-de-mesa.md) (modelo de mesa) y [ADR 0011](adr/0011-blackjack-primer-juego-de-mesas.md) (esta implementacion).

## Reglas
- Zapato de **6 barajas**. Las figuras valen 10; el as vale 11 y baja a 1 si te pasarías.
- **Blackjack natural** (as + 10 o figura en las dos primeras cartas): paga **3 a 2** (siempre en fichas enteras: apostando 15 se cobra 15 + 22 = 37).
- Ganar paga 1 a 1; **empate** devuelve lo apostado; pasarse de 21 pierde aunque el crupier también se pase.
- El crupier **planta en todos los 17** (también en un 17 blando) y pide con 16 o menos. Si tiene blackjack natural se muestra al repartir y nadie juega: los que tienen
  blackjack empatan y los demás pierden.
- Por ahora **sin doblar, dividir ni seguro**. Acciones: **pedir carta** o **plantarse**.

## Cómo es una mano
1. **Apuestas:** la mano está abierta pero sin reloj. La **primera apuesta** pone en marcha la ventana (10 s por defecto); se publica el *compromiso* del mazo antes de apostar.
   Sentarse es apostar: una apuesta por jugador y por mano, dentro del rango de la mesa. Si al cerrar nadie llegó a reservar, la mano vuelve a esperar.
2. **Reparto** (hasta 5 asientos, por orden de apuesta): dos cartas a cada jugador y al crupier una visible y una **tapada**.
3. **Turnos:** cada asiento decide en orden con un tiempo límite (15 s). Si no actúa, se planta. Con 21 se planta solo.
4. **Crupier:** da vuelta su carta tapada y pide hasta llegar a 17 o más (solo si quedó alguien por vencer).
5. **Pago y verificación:** se paga cada apuesta, se revela la semilla y empieza una pausa (5 s) antes de la siguiente mano.

## Provably fair (se puede verificar)
Antes de aceptar apuestas el servidor genera la semilla de la mano (32 bytes aleatorios, 64 caracteres hex) y **publica su hash**: `compromiso = SHA-256(semilla en UTF-8)`.
Al terminar la mano se revela la semilla y cualquiera puede recalcular el zapato completo:

```
clave    = semilla en UTF-8
bloque_k = HMAC-SHA256(clave, "blackjack:" + roundId sin guiones + ":" + k)      k = 0, 1, 2, ...
palabras = cada bloque da 8 enteros de 32 bits (big-endian), en orden
below(n) = tomar la siguiente palabra x; si x < 2^32 - (2^32 mod n) -> x mod n, si no se descarta y se toma otra   // sin sesgo
orden    = [0, 1, ..., 311]
para i = 311 hasta 1:  j = below(i + 1);  intercambiar orden[i] y orden[j]            // Fisher-Yates
zapato   = [orden[i] % 52 para cada i]                                                // carta 0..51
```
Una carta es un número de 0 a 51: `rango = carta % 13` (0 = as, 1 a 8 = 2 a 9, 9 a 12 = 10, J, Q, K) y `palo = carta / 13`.

**Orden del reparto** con N asientos: carta inicial del asiento s (s desde 0; en la API los asientos se numeran desde 1, o sea s = asiento - 1) = `zapato[s]`; carta visible del crupier = `zapato[N]`; segunda carta del asiento s = `zapato[N + 1 + s]`;
carta tapada = `zapato[2N + 1]`; las cartas que se piden salen desde `zapato[2N + 2]` (los jugadores en el orden en que piden y después el crupier).

La carta tapada **no se guarda ni se envía** hasta que se da vuelta (se calcula del zapato en ese momento), así que ni mirando la base de datos se la puede conocer antes: la semilla
está cifrada. Los mismos vectores de prueba se usan en el servidor (C#), en el navegador (TypeScript) y en una implementación independiente (Python):

| semilla | roundId | primeras 12 cartas | últimas 3 | suma(i x carta) mod 1.000.003 |
| --- | --- | --- | --- | --- |
| `blackjack-test-seed` | `00000000-0000-0000-0000-000000000001` | 0, 33, 7, 17, 6, 50, 45, 24, 22, 42, 40, 49 | 42, 26, 23 | 246087 |
| `9f2c…00ff` (64 hex) | `0190a1b2-c3d4-7e5f-8091-a2b3c4d5e6f7` | 44, 10, 1, 22, 35, 12, 0, 34, 18, 42, 4, 27 | 19, 4, 11 | 261287 |

## Apuestas y la Wallet
Es un juego más de la plataforma ([contrato](juegos-contrato.md)): **cada asiento es una apuesta** del protocolo de rondas (reservar → resolver → liquidar → cerrar) con su `BetId` e
`IdempotencyKey`; la Wallet no cambia. La reserva dura hasta el final de la mano (`ReserveStake.TtlSeconds`). Lo que se cobra incluye lo apostado (ganar = 2x, empate = 1x).
- Una apuesta que llega tarde (la mano ya reparte) o que no entra en la mesa llena **se devuelve** tal cual.
- Si el servicio se reinicia en medio de una mano, la mano se corta y **se devuelve lo apostado**.
- Una apuesta con fichas insuficientes se rechaza y no toma nada.

## Privacidad
Las cartas de todos los asientos son públicas en la mesa (como en un casino), pero **no se expone quién es cada jugador**: el estado de la mesa no trae ni identificador ni nombre
de nadie; solo tu propio asiento trae tu `betId`. El aviso en vivo no lleva cartas ni datos.

## Endpoints (jugador, bajo `/games/blackjack`)
| Endpoint | Descripción |
| --- | --- |
| `GET /tables` | Las mesas, su rango de apuesta, cuántos juegan y en qué fase está la mano |
| `GET /tables/{id}` | Estado de la mesa: la mano, el crupier (carta tapada oculta), los asientos y tu apuesta |
| `POST /tables/{id}/bets` (`Idempotency-Key`) | Sentarse y apostar `{ stake }` (202; la reserva en la Wallet es asíncrona) |
| `POST /bets/{id}/hit`, `POST /bets/{id}/stand` | Pedir carta / plantarse (solo en tu turno) |
| `GET /bets`, `GET /bets/{id}` | Tus apuestas (una ajena responde 404) |
| `GET /rounds/{id}` | Datos públicos de la mano para verificar: antes de terminar solo el compromiso; después, la semilla |

## Configuración (sección `Blackjack`)
`BettingSeconds` (7), `TurnSeconds` (15), `PauseSeconds` (3), `DealerStepMilliseconds` (500), `MaxSeats` (5), `EngineEnabled` (true) y `Tables`
(lista de `{ Id, Name, MinStake, MaxStake }`; por defecto `mesa-1` de 1 a 100, `mesa-2` de 10 a 1.000 y `mesa-3` de 100 a 10.000). Se valida al arrancar.
**El motor de mesas tiene que correr en UNA sola instancia** del servicio de juegos (`EngineEnabled`); las pruebas lo apagan y corren una mano a la vez.
