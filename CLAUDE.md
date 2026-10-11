# Pleno — Casino Online (fichas ficticias)

Laboratorio de arquitectura distribuida en .NET. Ver `README.md` y `docs/PLAN.md`.

## Stack
.NET 10 · Aspire 13 (CLI) · PostgreSQL + EF Core + Marten · Redis · RabbitMQ (luego Azure Service Bus) · Wolverine · OpenTelemetry · React + Vite + PixiJS · Keycloak · xUnit + Testcontainers + k6.

## Comandos
- Levantar todo: `aspire run` (requiere Docker Desktop corriendo)
- Build: `dotnet build Casino.slnx`
- Tests: `dotnet test Casino.slnx`
- Front (`web/`): `npm --prefix web run dev` · `test` · `lint` · `build` · `e2e` (el e2e necesita `aspire start`). Ver `docs/frontend.md`.

## Reglas
- Ramas siempre desde `dev`; `main` solo recibe merges de `dev`. Commits convencionales (`feat:`, `fix:`, `docs:`, `test:`, `chore:`).
- Montos de fichas como **enteros** (`long`); nunca `float`/`double`/`decimal` para saldos.
- El saldo se **deriva** de asientos de partida doble; nunca se edita directamente.
- Toda operacion que mueve fichas lleva `IdempotencyKey`.
- **Identidad:** Keycloak (ver `docs/autenticacion.md`). La identidad sale del token, nunca del cuerpo; las cuentas se derivan del usuario con `PlayerIds`.
- **Clave maestra del RNG** (`Fairness:MasterKey`): user-secrets en local (`dotnet user-secrets set ... --project src/Casino.GamesService`), Key Vault en la nube.
- **Nada de secretos** en codigo, `appsettings.json`, `.env` versionados ni pipelines: user-secrets en local, Key Vault en la nube.
- PII (email, nombre, documento) enmascarada en logs y fuera de trazas/metricas. Datos de prueba solo con Bogus.
- Tests obligatorios para todo lo que toque la Wallet (unitarios + integracion con Testcontainers).
- Cada fase cierra con un ADR en `docs/adr/`.
- Si digo "agregar a mejoras", anotarlo en `docs/mejoras.md` y ubicarlo en la seccion que corresponda (juego, Entorno, Billetera, Usuarios, Front / UX, Tiempo real, Rendimiento, Otros), reemplazando el `_(anotar)_` si es el primer item. Formato `- [ ] **Titulo.** detalle`.
- Si hago una pregunta con opciones y el usuario no contesta en 2 minutos, elegir siempre la opcion marcada como Recomendada y seguir (y avisar despues cual se eligio).

## Estructura
`src/Casino.AppHost` (con `realms/` de Keycloak), `src/Casino.Contracts`, `src/Casino.BuildingBlocks`, `src/Casino.Hosting` (autenticacion, JSON, datos y mensajeria comunes a los servicios), `src/Casino.ServiceDefaults`, `src/Casino.Api` (gateway: usuarios y tiempo real; reenvia lo demas con YARP), `src/Casino.WalletService` (servicio de la Wallet, base `walletdb`), `src/Casino.GamesService` (servicio de juegos, base `gamesdb`; carga los juegos habilitados), `src/Modules/{Wallet,Games,Games.Roulette,Games.Slots,Games.Crash,Realtime,Users,Promotions}` (un juego = un modulo que cumple el contrato de `docs/juegos-contrato.md`), `tests/`, `web/`, `docs/`. Cada servicio tiene su base de datos; entre servicios solo hay mensajes (`Casino.Contracts`) y HTTP por el gateway.
