# ADR 0003 - Identidad con Keycloak (OpenID Connect)

- **Estado:** reemplazada por el [ADR 0017](0017-identidad-propia.md) (identidad propia, sin Keycloak)
- **Fecha:** 2026-10-08

## Contexto
Hasta ahora la API confiaba en el `userId` y el `accountId` que mandaba el cliente en el cuerpo de la peticion: cualquiera podia
apostar con las fichas de otra cuenta. Hace falta saber QUIEN es quien llama, sin que nuestro codigo toque contraseñas.

## Decision
- **Keycloak** (servidor de identidad de codigo abierto) maneja registro, login, recuperacion de clave y emision de tokens.
  Corre como contenedor en el AppHost. Las contraseñas nunca pasan por la API.
- **OpenID Connect**: el navegador inicia sesion con el flujo *Authorization Code + PKCE* (cliente publico `casino-web`, sin
  secreto). La API es un servidor de recursos que solo valida tokens JWT (firma con las claves publicas del realm, emisor,
  destinatario `casino-api` y vigencia).
- **Roles del realm**: `player` y `backoffice`. Los lleva el claim `realm_access` y la API los traduce a roles de .NET.
  Todo usuario nuevo recibe `player` por defecto.
- **La identidad sale del token, nunca del cuerpo**: el `userId` es el `sub`. Un token sin `sub` valido se rechaza.
- **Una cuenta por usuario, derivada del usuario**: `PlayerIds.WalletAccountFor(userId)` (hash determinista). Se elimina el
  `accountId` de la API. No puede ser el mismo Guid que el usuario porque la Wallet y el generador de seeds comparten la tabla de
  streams de Marten y chocarian.
- **Alta automatica**: al primer request autenticado de un jugador se guarda su perfil y se publica `UserRegistered` (outbox,
  misma transaccion). La Wallet abre la cuenta y acredita las fichas de bienvenida (`Wallet:WelcomeChips`, 1000 por defecto).
  Todo idempotente.
- **Datos personales minimos**: email y nombre viven solo en Keycloak. Nuestro perfil guarda unicamente el id y la fecha de alta.
- **Endpoints por rol**: los jugadores solo ven lo suyo (`/wallet/me`, `/fairness/me`, rondas propias; una ronda ajena responde
  404). Reservar, liquidar y liberar dejan de ser endpoints: son pasos internos que ocurren por mensajes. El ajuste manual de
  saldo queda para `backoffice`.

## Consecuencias
- Con Keycloak local hay que mantener su realm (`src/Casino.AppHost/realms/casino-realm.json`). Una prueba arranca un Keycloak real
  con ese mismo archivo, asi un error en el realm rompe el CI en lugar de descubrirse al levantar el entorno.
- Los usuarios de desarrollo del realm tienen contraseñas triviales en un repositorio publico: son SOLO para desarrollo local y
  no deben reutilizarse jamas. La contraseña del administrador de Keycloak es un secreto de usuario (user-secrets), no esta en el repo.
- El cliente `casino-tests` permite pedir tokens con usuario y contraseña (*password grant*) unicamente para pruebas automatizadas.
- La cuenta de un jugador nuevo se abre de forma asincrona: el front debe tolerar un breve lapso en que `/wallet/me` responde 404.
- No hay limites de juego responsable por ahora: se evaluaran junto al agente de IA de la fase 5.
