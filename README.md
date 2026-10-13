# Pleno — Casino Online (laboratorio)

Casino online con **fichas ficticias** para practicar arquitectura distribuida con el ecosistema .NET. No usa dinero real, pagos ni retiros.

## Qué vamos a construir

Un saldo que nunca se pierde ni se duplica, con concurrencia, tiempo real, auditoría y un frontend llamativo. Arranca como **monolito modular** y en la fase 4 se extraen Wallet y Game Engine como servicios.

- **Wallet / Ledger:** partida doble, idempotencia, Event Sourcing.
- **Game Engine:** ruleta, tragamonedas y blackjack con RNG verificable (provably fair).
- **Realtime:** saldo y resultados en vivo con SignalR y Redis.
- **Lobby:** React + Vite + PixiJS.
- **Extras:** usuarios, bonos, backoffice y agentes de IA para juego responsable.

## Stack

.NET 10 · Aspire 13 · PostgreSQL + Marten · Redis · RabbitMQ (luego Azure Service Bus) · Wolverine · OpenTelemetry · React + PixiJS · ASP.NET Core Identity + OpenIddict · xUnit + Testcontainers + k6.

## Fases

| Fase | Objetivo |
| --- | --- |
| 0 | Setup: solución, Aspire, CI, Testcontainers |
| 1 | Wallet sólida |
| 2 | Primer juego (ruleta) y tiempo real |
| 3 | Frontend |
| 4 | Distribuir y endurecer |
| 5 | Extras (IA, blackjack, torneos, deploy) |

## Reglas del proyecto

- Ramas de trabajo siempre desde `dev`.
- Montos como enteros, nunca decimales flotantes.
- Ningún secreto en el repo: user-secrets en local y Key Vault en la nube.
- Tests obligatorios para todo lo que toque la Wallet.
- Datos de prueba siempre generados, nunca de personas reales.
