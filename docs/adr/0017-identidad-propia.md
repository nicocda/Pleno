# ADR 0017 - Identidad propia (reemplaza a Keycloak)

- **Estado:** aceptada (reemplaza al [ADR 0003](0003-identidad-con-keycloak.md))
- **Fecha:** 2026-10-13

## Contexto
Keycloak resolvia el registro, el login y los tokens, pero tiene un costo que pesa en el objetivo de correr todo en **un servidor chico 24/7** (4 GB de RAM, unos 6-10 USD al mes): es lo que mas memoria consume del
stack (600-900 MB), se importa como un realm que hay que mantener, y exige guardar nombre y apellido que el casino no necesita. Lo que hace falta es poco: alias publico, email para verificar y recuperar la cuenta, contraseña
o Google, roles y tokens que los demas servicios validen. Tambien se quiere dejar una puerta abierta a MFA y passkeys mas adelante.

## Decision
- **Un servicio de identidad propio** (`Casino.IdentityService` + modulo `Casino.Modules.Identity`, base `identitydb` en el mismo Postgres): **ASP.NET Core Identity** para las cuentas y **OpenIddict** para emitir tokens OpenID Connect.
  Los demas servicios no cambian de modelo: validan el JWT con las claves publicas (descubrimiento + JWKS), el `sub` sigue siendo la identidad y `PlayerIds` sigue derivando la cuenta del `sub`.
- **Datos personales minimos.** Del jugador solo se guarda un **alias** (unico, publico: lo ven el cajero y los demas) y el **email**. No hay nombre ni apellido. El email **nunca se guarda en claro**: va cifrado (AES-256-GCM, clave versionada
  para poder rotarla sin recifrar todo) y con un **HMAC-SHA256 con otra clave** para buscarlo y garantizar que sea unico sin descifrar. El token solo lleva `sub`, `preferred_username` (el alias) y `role`: nunca el email. Las claves son
  secretos (user-secrets en local; Key Vault o un `.env` con permisos 600 en el servidor).
- **Email obligatorio y verificado**, porque es la unica forma de recuperar una cuenta (y de agregar MFA mas adelante). Sin verificarlo no se entra. Verificar y recuperar usan **tokens de un solo uso** (de los que solo se guarda el hash, con vencimiento).
  Las respuestas no revelan si un email tiene cuenta: registrar un email repetido responde igual que un alta y le avisa por correo al dueño.
- **Google se mantiene** (es la forma mas simple de empezar a usar la aplicacion). Las credenciales de la app OAuth son secretos y sin ellas no hay boton. Google devuelve al front un **codigo de un solo uso** (2 minutos) que el front cambia
  por tokens: los tokens nunca viajan por la URL. Solo se vuelve a origenes de una lista (`Identity:AllowedReturnOrigins`). Si Google trae el email de una cuenta **sin verificar**, esa cuenta pasa a ser de quien demostro tener el email y **pierde la
  contraseña** (defensa contra quien se registra con el email de otra persona). Las cuentas de Google reciben un alias generado (`jugador_xxxxxx`); no se guarda ni el nombre.
- **Flujos:** el front propio entra con **password grant** (alias o email + contraseña) y renueva con **refresh token con rotacion**; es un cliente de primera parte (`casino-web`), asi que no hace falta la pantalla de consentimiento de Authorization Code.
  Access token de 10 minutos; refresh de 14 dias; la sesion vive en `sessionStorage`. Un 401 cierra la sesion y vuelve la pantalla de entrada.
- **Roles** en el claim plano `role` (`player`, `backoffice`, `cashier`, `head_cashier`). Se asignan desde `/admin/roles` (endpoint `PUT /identity/admin/users/{id}/roles`, solo `backoffice`); al cambiar roles se cierran las sesiones de esa cuenta. El primer
  administrador sale de `Identity:AdminAliases`. Un administrador no puede quitarse su propio rol.
- **Defensas:** contraseñas de 10 o mas caracteres sin reglas de composicion (recomendacion NIST), hash PBKDF2 de ASP.NET Core Identity, bloqueo tras 5 intentos fallidos (15 minutos), limite de peticiones por IP en registro y login.
- **Desarrollo:** el servicio siembra los mismos 5 usuarios que tenia el realm (mismos ids, alias y claves) solo en `Development` y con `Identity:SeedDevUsers=true`. Las pruebas de carga pueden sembrar `load-001..N`.

## Consecuencias
- Un contenedor menos y unos 600-900 MB de RAM libres; el stack entra en una VPS de 4 GB.
- Ahora **nosotros** somos responsables de lo que Keycloak hacia: hash de claves, bloqueo, verificacion y recuperacion de cuenta, rotacion de refresh tokens y de claves de firma. Se cubre con pruebas de integracion contra un Postgres real
  (registro, login, bloqueo, verificacion, recuperacion, Google, roles) y con las dependencias estandar de .NET; no se escribe criptografia propia.
- **Perder la clave de cifrado del email deja ilegibles los emails guardados** (no los alias ni el login por alias): se respalda aparte de los backups de la base. La clave del HMAC no es versionada: rotarla obliga a recalcular los hashes.
- Las tablas se crean con `EnsureCreated` al arrancar (laboratorio). Antes de produccion real conviene pasar a migraciones de EF Core.
- No hay MFA todavia. Quedan anotados en `docs/mejoras.md`: MFA/passkeys, refresh token en cookie `HttpOnly` (hoy esta en `sessionStorage`, expuesto a XSS) y rotacion programada de claves.
- El front pierde la dependencia `oidc-client-ts`; el cliente de identidad es chico y propio (`web/src/auth/identity.ts`).
- Detras de un proxy (Caddy) el servicio confia en `X-Forwarded-*`: su puerto no debe estar expuesto fuera del proxy.
