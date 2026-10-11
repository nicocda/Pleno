# Proyecto Casino Online — Plan de trabajo

Oct 8, 2026 · @Nicolas Giordano

## Objetivo y alcance

Construir un casino online con fichas ficticias como laboratorio para practicar arquitectura distribuida y tecnologías actuales del ecosistema .NET. El dominio obliga a resolver bien lo difícil: un saldo que nunca puede perderse ni duplicarse, concurrencia, tiempo real, auditoría, seguridad y un frontend atractivo.

Qué se busca practicar:

- Mensajería real con RabbitMQ y Azure Service Bus (hoy sin experiencia previa).
- Microservicios realmente distribuidos y consistencia entre servicios (outbox, sagas, compensaciones).
- Event Sourcing, observabilidad con OpenTelemetry y orquestación con .NET Aspire.
- Desarrollo asistido por IA con Claude Code y features de IA dentro del producto.

Fuera de alcance: dinero real, pasarelas de pago y retiros. Operar con dinero real exige licencia de juego provincial, KYC y controles antilavado; el proyecto se mantiene con fichas sin valor monetario.

## Módulos del dominio

La Wallet es el corazón del sistema: todo lo demás mueve fichas a través de ella.

| Módulo | Responsabilidad | Qué se practica |
| --- | --- | --- |
| Wallet / Ledger | Saldo de fichas con partida doble; reservas, liquidaciones y reversas | Idempotencia, concurrencia, Event Sourcing, auditoría |
| Game Engine | Tragamonedas, ruleta y blackjack con RNG verificable | Provably fair, lógica de dominio pura, testing |
| Lobby / Frontend | Catálogo de juegos, mesas, historial, animaciones | React + PixiJS, UX, tiempo real |
| Realtime | Saldo y resultados en vivo, mesas multijugador | SignalR con backplane Redis |
| Usuarios | Registro, login, límites de juego responsable | Identidad (Keycloak o Entra External ID), protección de PII |
| Bonos y promociones | Fichas de bienvenida, bonos diarios, torneos | Reglas configurables, eventos, jobs programados |
| Backoffice | Reportes, auditoría, ajustes manuales con doble aprobación | Proyecciones de lectura (CQRS), roles |
| Juego responsable (IA) | Detectar patrones de riesgo y aplicar límites | Agentes de IA sobre eventos del dominio |

## Arquitectura y stack

Se arranca como monolito modular y se extraen Wallet y Game Engine como servicios en la fase 4. Así se practican ambos mundos y queda documentado por qué se separó cada pieza, que es lo que se defiende en una conversación de arquitectura.

&#91;embedded content: arquitectura objetivo · fase 4\]

La API solo inicia operaciones; Wallet y Game Engine se coordinan por eventos y Realtime los reenvía al navegador.

| Capa | Elección | Nota |
| --- | --- | --- |
| Runtime | .NET 10 (LTS), ASP.NET Core Minimal APIs | SDK 10.0.x fijado en global.json; .NET 11 sigue en preview |
| Orquestación local | Aspire 13.x (Aspire CLI) | Levanta Postgres, Redis, RabbitMQ y servicios con un solo comando |
| Base de datos | PostgreSQL + EF Core; Marten para el ledger | Marten da Event Sourcing sobre Postgres |
| Caché y tiempo real | Redis | Sesiones, leaderboards, rate limiting, backplane de SignalR |
| Mensajería | RabbitMQ en local, Azure Service Bus en la nube | Wolverine (open source, integra con Marten y outbox). MassTransit v9 es comercial: descartado |
| Resiliencia | Polly v8 (Microsoft.Extensions.Resilience) | Reintentos, circuit breaker, timeouts |
| Observabilidad | OpenTelemetry + dashboard de Aspire; Grafana o Application Insights | Trazas que cruzan la cola de mensajes |
| Frontend | React + Vite + PixiJS para los juegos | Phaser como alternativa para juegos más complejos |
| Identidad | Keycloak en local o Entra External ID | OIDC, JWT, roles para backoffice |
| Testing | xUnit, Testcontainers, k6 | Integración con infraestructura real y carga |
| Nube | Azure Container Apps, Key Vault, Azure DevOps o GitHub Actions | Infra como código con Bicep |

## Patrones clave a implementar

Cada patrón responde a un riesgo concreto del negocio; ninguno se agrega por moda.

| Patrón | Riesgo que cubre | Cómo se aplica |
| --- | --- | --- |
| Partida doble | Fichas que aparecen o desaparecen | Cada movimiento son asientos que suman cero; el saldo se deriva, nunca se edita |
| Idempotencia | Reintentos que debitan dos veces | Toda apuesta lleva un IdempotencyKey con índice único; un duplicado devuelve el resultado original |
| Concurrencia optimista | Dos apuestas simultáneas dejan saldo negativo | Versión por cuenta; conflicto implica reintento controlado |
| Transactional Outbox | Debitar sin publicar el evento, o al revés | El evento se guarda en la misma transacción que el débito y un relay lo publica |
| Inbox / consumidor idempotente | Mensajes entregados más de una vez | Registro de mensajes procesados por consumidor |
| Saga con compensación | Falla el juego después de reservar fichas | Reservar, jugar, liquidar; si algo falla, se libera la reserva |
| Event Sourcing | Perder la historia de un saldo | El ledger es un stream de eventos por cuenta, con snapshots |
| CQRS | Reportes que bloquean la operación | Proyecciones de lectura separadas para backoffice e historial |
| Provably fair | Desconfianza en el resultado | Hash del seed del servidor publicado antes de jugar, combinado con seed del cliente y nonce |
| Dead-letter queue | Mensajes envenenados que traban la cola | Reintentos con backoff y DLQ con alerta |

El flujo de una apuesta queda así:

1. El cliente envía la apuesta con su IdempotencyKey.
2. Wallet reserva las fichas y publica la reserva vía outbox.
3. Game Engine resuelve la jugada con RNG verificable y publica el resultado.
4. Wallet liquida: libera la reserva y acredita el premio si corresponde.
5. Realtime notifica saldo y resultado al cliente por SignalR.
6. Si el paso 3 falla o expira, la saga compensa liberando la reserva.

## Plan por fases

Seis fases de 1 a 2 semanas cada una; la fase 1 sola ya deja material sólido para mostrar. Cada fase cierra con su código en dev y una nota de decisiones (ADR).

### Fase 0 — Setup (2 a 3 días)

- [x] Repo con rama dev como base, protección de main y convención de commits
- [x] Solución .NET 10 con AppHost de Aspire y ServiceDefaults
- [x] Postgres, Redis y RabbitMQ levantados desde Aspire
- [x] CI: build, tests y análisis estático en cada PR
- [x] Proyecto de tests de integración con Testcontainers
- [x] CLAUDE.md con convenciones del proyecto para trabajar con Claude Code
- [x] Carpeta docs/adr con la primera decisión: monolito modular primero

### Fase 1 — Wallet sólida (1 a 2 semanas)

- [x] Modelo de cuentas y asientos de partida doble
- [x] Operaciones: acreditar, reservar, liquidar, liberar reserva, revertir
- [x] IdempotencyKey con índice único y respuesta reproducible
- [x] Concurrencia optimista por cuenta
- [x] Ledger como Event Stream con Marten y snapshots
- [x] Test de carga concurrente: 1.000 apuestas en paralelo y el saldo cierra exacto
- [x] Endpoint de auditoría: reconstruir el saldo de una cuenta a cualquier fecha

### Fase 2 — Primer juego y tiempo real (1 a 2 semanas)

- [x] RNG provably fair: server seed hasheado, client seed y nonce (seeds por usuario con rotación, nonce asignado por el servidor y cifrado en reposo)
- [x] Ruleta europea como primer juego (lógica pura, 100% testeada) con todas las apuestas
- [x] Outbox en Wallet e Inbox en Game Engine sobre RabbitMQ
- [x] Saga de apuesta con timeout y compensación (coreografiada, ver ADR 0002)
- [x] Hub de SignalR para saldo y resultados en vivo (con backplane de Redis y avisos autenticados)
- [x] Página de verificación de jugadas para el usuario (estática en /verify, calcula todo en el navegador)

### Fase 3 — Frontend llamativo (2 semanas)

- [x] Lobby con catálogo de juegos y saldo en vivo
- [x] Ruleta animada con PixiJS (tapete completo con 157 apuestas, rueda con bola que cae en el número que ya decidió el servidor)
- [x] Tragamonedas con tabla de pagos configurable (3 rodillos, retorno exacto validado al arrancar, ver ADR 0006)
- [x] Tragamonedas con cascadas y multiplicador creciente, retorno exacto incluyendo las cascadas (ver ADR 0013)
- [x] Historial de jugadas y movimientos (jugadas con verificación y extracto de movimientos de saldo derivado del ledger; el admin tiene el historial general de cargas)
- [ ] Animaciones de premios, sonido y modo oscuro (modo oscuro hecho; faltan animaciones de premios y sonido)
- [x] Login con Keycloak o Entra External ID (Keycloak)

### Fase 4 — Distribuir y endurecer (2 semanas)

- [x] Extraer Wallet y Game Engine como servicios independientes (hecho: Wallet y juegos, cada uno con su base, gateway YARP y pruebas distribuidas; ver ADR 0007 y 0008)
- [ ] Resiliencia con Polly: reintentos, circuit breaker, timeouts
- [ ] Dead-letter queues con alertas
- [ ] Trazas de OpenTelemetry de punta a punta a través de la cola
- [x] Pruebas de carga con k6 y métricas de latencia p95/p99 (`tests/load`, línea base y comparación en `docs/pruebas-de-carga.md`)
- [ ] Chaos: tirar Game Engine a mitad de apuesta y verificar que el saldo cierra
- [ ] Cambiar RabbitMQ por Azure Service Bus sin tocar el dominio

### Fase 5 — Extras modernos (abierta)

- [ ] Agente de juego responsable que analiza eventos y propone límites
- [ ] Agente de soporte que consulta historial y explica jugadas
- [x] Crash: juego en vivo con ronda compartida y provably fair por ronda (ADR 0009, `docs/juego-crash.md`)
- [x] Blackjack: primer juego de mesas (mesas fijas contra el crupier, varios jugadores por mesa, mazo provably fair; ADR 0011, `docs/juego-blackjack.md`)
- [x] Plataforma de mesas entre jugadores (mesas de jugador publicas y privadas, bots, turnos, fichas en la Wallet) y Uno como primer juego (ADR 0012, `docs/juegos-mesas.md`, `docs/juego-uno.md`)
- [x] Truco a dos jugadores sobre la plataforma de mesas (envido, truco, retruco, vale cuatro; `docs/juego-truco.md`)
- [x] Poker (Texas Hold'em de una mano por mesa) sobre la plataforma de mesas entre jugadores (`docs/juego-poker.md`)
- [ ] Torneos con leaderboard en Redis
- [ ] Deploy en Azure Container Apps con Bicep
- [ ] Backoffice con ajustes manuales y doble aprobación

## Seguridad, PII y secretos

Aunque las fichas no valgan nada, los datos de usuario se tratan como reales desde el día uno.

- **PII:** email, nombre, fecha de nacimiento y documento (si se simula KYC) se encriptan en reposo, se enmascaran en logs y nunca viajan en trazas ni métricas de OpenTelemetry.
- **Datos de prueba:** siempre generados con Bogus; nunca datos de personas reales.
- **Secretos:** connection strings, claves del RNG y tokens van en user-secrets en local y Azure Key Vault en la nube, con Managed Identity. Nunca en el repo, en appsettings.json ni en variables de pipeline sin proteger.
- **Escaneo:** secret scanning y push protection activos en el repo; .gitignore cubre .env y archivos de secretos locales.
- **Server seed del RNG:** se guarda cifrado y solo se revela después de rotarlo, para que la verificación sea posible sin permitir predecir jugadas.
- **Backoffice:** ajustes manuales de saldo con doble aprobación y registro de auditoría inmutable.
- **API:** rate limiting por usuario, validación estricta de montos (enteros, sin decimales flotantes) y autorización por recurso.

## Estructura de repo y cómo seguir en Claude Code

Exportar este doc como Markdown a docs/PLAN.md del repo y usarlo como punto de partida en Claude Code.

```
casino/
  src/
    Casino.AppHost/            # Aspire
    Casino.ServiceDefaults/
    Modules/
      Wallet/                  # Domain, Application, Infrastructure, Api
      Games/
      Realtime/
      Users/
      Promotions/
    Casino.Api/                # host del monolito modular
  web/                         # React + Vite + PixiJS
  tests/
    Wallet.Tests/
    Games.Tests/
    Integration.Tests/         # Testcontainers
    load/                      # scripts k6
  docs/
    PLAN.md
    adr/
  CLAUDE.md
```

Primer pedido sugerido para Claude Code: leer docs/PLAN.md, crear la rama de la fase 0 desde dev y armar el esqueleto de la solución con Aspire. El CLAUDE.md debería fijar desde el inicio: ramas siempre desde dev, montos como enteros, nada de secretos en el código y tests obligatorios para todo lo que toque la Wallet.
