# RabbitMQ en Pleno y conceptos de EDD

Guia conceptual de como se usa RabbitMQ en el sistema, como se compara con Kafka y que conceptos de la arquitectura dirigida por eventos (EDD) conviene tener claros. Fuentes: `src/Casino.Hosting/MessagingTopology.cs`, los `*Messaging.cs` de cada servicio y el [ADR 0002](adr/0002-coreografia-con-wolverine-y-rabbitmq.md).

## 1. Como se usa RabbitMQ

Broker: **RabbitMQ**, con **Wolverine** (sobre Marten) como libreria de mensajeria. En la nube se pasara a Azure Service Bus.

```
 ┌──────────────┐  ReserveStake / RoundResolved / ExtendReservation
 │ GamesService │ ───────────────► [queue] wallet.commands ───────┐
 │ (rondas)     │                  (1 cola, 1 dueño)              ▼
 └──────▲───────┘                                          ┌─────────────┐
        │                                                  │ WalletService│
        │  StakeReserved/Rejected/Settled/Released...      │ (fichas)     │
        │  ◄─ [queue] games.wallet-events ◄─ (fanout) ◄────┤  publica     │
        │                wallet.stake-events               │  hechos      │
        │                                                  └──┬───────▲───┘
        │                                  BalanceChanged     │       │ UserRegistered
        │                          (fanout) wallet.balance-events     │ [queue] wallet.user-events
        │                                                     ▼       │ ◄─ (fanout) users.events
 ┌──────┴───────┐  RoundClosed  (fanout) games.events   ┌────────────┴──┐
 │ GamesService │ ─────────────────────────────────────►│  Casino.Api   │
 │              │  GameBroadcast (fanout) games.broadcast│ (gateway +    │
 └──────────────┘ ─────────────────────────────────────►│  tiempo real) │──► SignalR ─► navegador
                      → colas realtime.*                 └───────────────┘
```

### Topologia

| Elemento | Tipo | Productor | Consumidor |
|---|---|---|---|
| `wallet.commands` | cola directa | Games (y la Wallet a si misma: `ExpireReservation`) | Wallet |
| `wallet.stake-events` | exchange fanout | Wallet | cola `games.wallet-events` (Games) |
| `wallet.balance-events` | exchange fanout | Wallet | cola `realtime.wallet-events` (Api) |
| `games.events` | exchange fanout | Games | cola `realtime.game-events` (Api) |
| `games.broadcast` | exchange fanout | Games | cola `realtime.game-broadcast` (Api) |
| `users.events` | exchange fanout | Api (`UserRegistered`) | cola `wallet.user-events` (Wallet) |

Dos patrones de ruteo:

- **Ordenes (comandos):** una cola directa con un solo dueño. Mensajes en imperativo (`ReserveStake`).
- **Hechos (eventos):** un exchange `fanout` por tema, con una cola por consumidor. Mensajes en pasado (`StakeReserved`). Sumar un consumidor es agregar una cola al exchange; el productor no cambia.

### Garantias

| Pieza | Para que sirve |
|---|---|
| **Outbox transaccional** (Wolverine + Marten) | El mensaje se guarda en la misma transaccion que el cambio de estado. Si el commit falla no sale nada; si sale bien, el mensaje queda garantizado aunque RabbitMQ este caido. |
| **Inbox durable** en cada listener | Guarda lo recibido y evita procesar dos veces el mismo mensaje. |
| **`IdempotencyKey`** (`reserve:{betId}`, `settle:{betId}`) | La entrega es "al menos una vez"; un duplicado no tiene efecto. |
| **Topologia declarada por todos los servicios** | `Declare` es idempotente: el orden de arranque no importa y no se pierden mensajes por publicar antes de que exista la cola. |
| **`ExpireReservation`** | La Wallet se manda un mensaje diferido a si misma para liberar reservas vencidas. |

El diseño es **coreografia**, no orquestacion: no hay una saga central. La Wallet es dueña de las fichas, cada juego es dueño de su ronda, y agregar un juego no toca la Wallet.

### Flujo de una apuesta

```
POST /games/<juego>/bets  (Idempotency-Key)
  1. Una transaccion: se asigna el nonce, se guarda la ronda (Placed) y se encola ReserveStake (outbox)
  2. Wallet reserva  -> StakeReserved (y programa ExpireReservation)
     o rechaza       -> StakeRejected -> ronda Rejected
  3. Games sortea, guarda el resultado (Resolved) y encola RoundResolved (misma transaccion)
  4. Wallet liquida  -> StakeSettled -> ronda Settled
     o rechaza (reserva vencida) -> StakeSettlementRejected -> ronda Voided
  Si nadie liquida a tiempo: ExpireReservation -> StakeReleased -> ronda Voided
```

## 2. RabbitMQ vs Kafka

| | **RabbitMQ** (lo que usamos) | **Kafka** |
|---|---|---|
| Modelo | Broker inteligente: exchanges, bindings y colas. Los mensajes se **borran** al confirmarse. | Log distribuido y persistente. Los mensajes **se quedan** y cada consumidor lleva su offset. |
| Ruteo | Muy flexible (direct, fanout, topic, headers). | Basico: topics y particiones. |
| Mensajes diferidos y reintentos | Faciles (TTL, dead-letter, delay en Wolverine). | No son nativos. |
| Orden | Por cola. | Garantizado por particion. |
| Reprocesar historia | No: el mensaje ya se consumio. | Si: se puede rebobinar el offset. |
| Throughput | Decenas de miles de msg/s. | Cientos de miles o millones de msg/s. |
| Operacion | Simple; un nodo alcanza. | Mas pesada (cluster, particiones, retencion). |
| Caso tipico | Comandos, tareas y workflows entre servicios. | Streaming de eventos, analitica, event sourcing a gran escala. |

### Por que RabbitMQ para este proyecto

- Hay comandos con un solo dueño (`ReserveStake`) y mensajes diferidos (`ExpireReservation`), que RabbitMQ resuelve de forma natural.
- El volumen es bajo y la complejidad operativa pesa.
- Azure Service Bus, el destino en la nube, se parece conceptualmente a RabbitMQ y no a Kafka; con Wolverine la migracion es barata.

### Cuando Kafka tendria sentido

Si se quisiera un log historico de apuestas para analitica, antifraude o reproducir el pasado. Hoy ese rol lo cumple Marten, que guarda los eventos de la Wallet.

## 3. Conceptos basicos de EDD

- **Evento (hecho):** algo que ya paso y no se discute (`StakeReserved`). Se nombra en pasado.
- **Comando:** una peticion a un destinatario concreto, que puede rechazarla (`ReserveStake`). Se nombra en imperativo.
- **Productor / consumidor:** el productor no sabe quien escucha. Eso da desacople.
- **Broker:** intermediario que guarda y entrega los mensajes (RabbitMQ).
- **Coreografia vs orquestacion:** en la coreografia cada servicio reacciona a eventos; en la orquestacion un coordinador central dice que sigue. Aca se eligio coreografia.
- **Consistencia eventual:** el estado de un servicio se entera del de otro unos milisegundos despues. Es el precio del desacople.
- **Entrega "al menos una vez":** puede haber duplicados. Se resuelve con **idempotencia**: procesar dos veces da el mismo resultado.
- **Outbox / Inbox:** el outbox resuelve el "dual write" (guardar en la base y publicar son dos operaciones que pueden fallar por separado); el inbox deduplica en el receptor.
- **Compensacion:** en lugar de un rollback distribuido se hace la accion inversa. Si la reserva vence, se libera y la ronda queda `Voided`.
- **Event sourcing:** cosa distinta pero compatible. Se guardan los eventos como fuente de verdad. La Wallet lo hace con Marten: el saldo se deriva de los asientos.
- **Dead-letter queue:** cola donde van los mensajes que fallan reiteradamente, para revisarlos sin bloquear al resto.
- **Costo a pagar:** el flujo ya no esta en un solo lugar. Por eso importan las trazas de OpenTelemetry y los tests de extremo a extremo.
