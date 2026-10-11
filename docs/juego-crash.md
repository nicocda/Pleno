# Crash

Un cohete sube con un multiplicador (x1,00, x1,01, ...) y **explota en un momento que decide el servidor**. El jugador apuesta durante una ventana
corta, y retira (a mano, o con un retiro automatico) antes de que explote: cobra `apuesta x multiplicador`. Si explota antes, pierde lo apostado.
La ronda es **compartida**: todos los jugadores ven el mismo cohete a la vez. Decision y motivos: [ADR 0009](adr/0009-crash-ronda-compartida-en-vivo.md).

## Como es una ronda
1. **Apuestas** (por defecto 8 s): se publica el *compromiso* (SHA-256 de la semilla de la ronda) y se aceptan apuestas.
2. **Sube**: el multiplicador crece como `x(t) = e^(0,07 t)` (a los 10 s va por x2). El jugador retira cuando quiere.
3. **Explota**: se revela la semilla, pierden las apuestas que seguian en juego y se abre una pausa (4 s) antes de la siguiente ronda.

El servidor decide el multiplicador con **su reloj** (no el del cliente). Un retiro que llega despues de la explosion se rechaza (la apuesta se pierde).
Los tiempos salen de la hora de inicio y de la formula, no de contar "ticks": un retraso del servidor no corre el resultado.

## Provably fair (se puede verificar)
Antes de aceptar apuestas el servidor genera la semilla (`serverSeed`: 32 bytes aleatorios, 64 caracteres hex) y **publica su hash**:
`compromiso = SHA-256(serverSeed en UTF-8)`. Asi nadie (ni el casino) puede cambiar el resultado despues de ver las apuestas. Al explotar se revela
la semilla y cualquiera puede recalcular el punto:

```
mac    = HMAC-SHA256(clave = serverSeed en UTF-8, mensaje = "crash:" + roundId sin guiones)
r      = (primeros 7 bytes de mac, como entero big-endian) >> 4        // 52 bits: uniforme en [0, 2^52)
punto  = piso( 100 * (1000 - ventaja) * 2^52 / (1000 * (2^52 - r)) )   // en centesimas; ventaja en milesimas (por defecto 30 = 3 %)
punto  = min(max(punto, 100), 10000)                                   // entre x1,00 (explosion instantanea) y x100 (tope)
```

Todo con enteros (sin decimales). Con la ventaja del 3 %, un jugador que siempre retira en el mismo multiplicador recupera en promedio ~97 %
(`P(el cohete pasa de m) = 0,97 / m`). La pagina del juego trae un boton **"Verificar en mi navegador"** que hace este calculo con WebCrypto y BigInt, y los
mismos vectores de prueba se usan en el servidor (C#), en el navegador (TypeScript) y en una implementacion independiente (Python).

Pago: `piso(apuesta x multiplicador / 100)` fichas, siempre un entero.

## Apuestas y la Wallet
Es un juego mas de la plataforma ([contrato](juegos-contrato.md)): sigue el protocolo de rondas con la Wallet (reservar → resolver → liquidar → cerrar), con una
particularidad: la reserva tiene que durar **hasta el final de la ronda aunque suba hasta el tope (~66 s)**, mucho mas que el plazo por defecto (60 s). Por eso la
orden `ReserveStake` lleva un plazo propio (`TtlSeconds`, acotado a 1 a 3.600 s) que la Wallet respeta.
- Una apuesta que llega tarde (la reserva se confirma cuando ya no se aceptan apuestas) **se devuelve** tal cual.
- Si el servicio se reinicia en medio de una ronda, la ronda se corta y **se devuelve lo apostado**: nadie pierde por una caida.
- Una apuesta con fichas insuficientes se rechaza y no toma nada.

## Tiempo real
Los cambios de fase (se abrio, empezo a subir, exploto) se difunden a **todos los jugadores conectados** por SignalR (el servicio de juegos los publica por RabbitMQ
como `GameBroadcast` y el gateway los reenvia). El navegador no recibe cada tick: dibuja el multiplicador con la hora del servidor y la formula, y consulta
`GET /games/crash/state` cada segundo como respaldo. Los avisos de fase no llevan datos de ningun jugador ni la semilla antes de explotar.

## Endpoints (jugador, bajo `/games/crash`)
| Endpoint | Descripcion |
| --- | --- |
| `GET /state` | La ronda en curso, mi apuesta y las ultimas explosiones |
| `POST /bets` (`Idempotency-Key`) | Apostar `{ stake, autoCashOut? }` en la ronda abierta (202; la reserva en la Wallet es asincrona) |
| `POST /bets/{id}/cashout` | Retirar en el multiplicador actual (lo decide el servidor) |
| `GET /bets`, `GET /bets/{id}` | Mis apuestas (una ajena responde 404) |
| `GET /rounds`, `GET /rounds/{id}` | Datos publicos para verificar: antes de explotar solo el compromiso; despues, la semilla y el punto |

## Configuracion (seccion `Crash`)
`BettingSeconds` (8), `PauseSeconds` (4), `GrowthPerSecond` (0,07), `EdgePermille` (30), `MinStake` (1), `MaxStake` (10.000), `EngineEnabled` (true).
Se valida al arrancar. **El motor de rondas tiene que correr en UNA sola instancia** del servicio de juegos (`EngineEnabled`); las pruebas lo apagan y corren una ronda a la vez.
