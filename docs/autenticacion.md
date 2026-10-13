# Autenticacion: como usarla en desarrollo

Decision y motivos: [ADR 0017](adr/0017-identidad-propia.md) (reemplaza al [ADR 0003](adr/0003-identidad-con-keycloak.md), Keycloak).

## Como esta armada
`Casino.IdentityService` (modulo `src/Modules/Identity`, base `identitydb`) es el unico dueño de las cuentas: **alias** (publico, unico), **email** (obligatorio, verificado, guardado **cifrado** y con un **HMAC** para buscarlo) y
contraseña con hash. Emite tokens OpenID Connect (OpenIddict): el JWT lleva `sub`, `preferred_username` (el alias), `role` y `aud=casino-api`; **nunca el email**. Los demas servicios solo validan el JWT con las claves publicas
(`/.well-known/openid-configuration` + JWKS). En local escucha en **http://localhost:8080** (el mismo puerto que usaba Keycloak).

## Levantar todo
`python scripts/dev-identity-secrets.py` (una vez) y despues `aspire run` (o `aspire start`): levanta Postgres, Redis, RabbitMQ, el servicio de identidad, la Wallet, los juegos, la API y el front.

## Secretos que necesita el entorno local
Ninguno se versiona. Se guardan con user-secrets:

| Secreto | Proyecto | Para que |
| --- | --- | --- |
| `Parameters:identity-email-key` | `src/Casino.AppHost` | 32 bytes en base64: cifra los emails (AES-256-GCM). **Si se pierde, los emails guardados quedan ilegibles** |
| `Parameters:identity-email-hmac-key` | `src/Casino.AppHost` | 32 bytes en base64, distinta a la anterior: hash con clave para buscar emails sin descifrar |
| `Fairness:MasterKey` | `src/Casino.GamesService` | Clave maestra que cifra las server seeds (32 bytes en base64) |
| `Parameters:google-client-id` / `google-client-secret` | `src/Casino.AppHost` | **Opcionales**: login con Google (ver abajo) |

`python scripts/dev-identity-secrets.py` genera las dos primeras (sin pisarlas si ya existen). La de las seeds:

```
dotnet user-secrets set "Fairness:MasterKey" "<32 bytes aleatorios en base64>" --project src/Casino.GamesService
```

En la nube o en un servidor: los mismos valores van en Key Vault o en un `.env` con permisos 600 (nunca versionado), mas `Identity:SigningKeyPem` (RSA en PEM, firma de los tokens), `Identity:TokenEncryptionKey` (32 bytes en base64,
cifra los refresh tokens), `Identity:Issuer` (la URL publica del servicio), `Identity:PublicWebUrl` / `Identity:AllowedReturnOrigins` (donde vive el front) y `Email:Smtp:*` (host, puerto, usuario y clave del correo). En `Development`,
si faltan las claves de firma y de cifrado de tokens, se generan en memoria (los tokens dejan de valer al reiniciar). Fuera de `Development` el servicio **no arranca** sin ellas ni sin SMTP.

## Entrar con Google (opcional)
1. En [Google Cloud Console](https://console.cloud.google.com/apis/credentials) crear una credencial **ID de cliente de OAuth** de tipo *Aplicacion web*.
   Origen autorizado: `http://localhost:8080`. URI de redireccion autorizada: `http://localhost:8080/signin-google` (en modo red local, `https://<ip>:5173/signin-google`).
2. Guardar el ID y el secreto con user-secrets (nunca en el repositorio):
   ```
   dotnet user-secrets set "Parameters:google-client-id" "<id>" --project src/Casino.AppHost
   dotnet user-secrets set "Parameters:google-client-secret" "<secreto>" --project src/Casino.AppHost
   ```
3. Reiniciar con `aspire run`: la pantalla de entrada muestra **Entrar con Google**. Flujo: el front va a `/identity/google/start` → Google → `/signin-google` → el servicio vuelve al front con un **codigo de un solo uso**
   (2 minutos) que el front cambia por tokens en `/connect/token` (`grant_type=urn:casino:grant_type:external_code`). Los tokens no viajan por la URL y solo se vuelve a origenes de `Identity:AllowedReturnOrigins`.

Sin esas credenciales el boton no aparece (`GET /identity/providers` dice `google: false`). En la nube las mismas dos credenciales van en Key Vault.

> **Cuentas repetidas:** si el email de Google ya tiene una cuenta con contraseña **verificada**, el login de Google se vincula a esa misma cuenta (conserva alias y roles). Si la cuenta **no estaba verificada**, pasa a ser de quien demostro tener el email
> y **pierde la contraseña** (defensa contra el alta previa con el email de otra persona). Las cuentas nuevas por Google reciben un alias generado (`jugador_xxxxxx`); no se guarda el nombre de Google. El email es PII: va cifrado y se enmascara en logs.

## Registro, verificacion y recuperacion
- **Registro:** `POST /identity/register` `{alias, email, password}`. Alias de 3 a 20 caracteres (letras, numeros, `.`, `-`, `_`), contraseña de 10 o mas. Responde 201 **tambien si el email ya tenia cuenta** (no se revela quien esta registrado; el dueño recibe un aviso).
- **Verificar el email:** el correo trae un enlace `/verificar?token=...` (vale 24 h, un solo uso). **Sin verificar no se entra** (`error_description=email_not_verified`). `POST /identity/verify-email`, `POST /identity/resend-verification`.
- **Recuperar la clave:** `POST /identity/forgot-password` (siempre 202) manda `/recuperar?token=...` (vale 1 h, un solo uso); `POST /identity/reset-password` cambia la clave, verifica el email, levanta el bloqueo y **cierra las demas sesiones**.
- **Defensas:** bloqueo de la cuenta tras 5 intentos fallidos (15 minutos) y limite de peticiones por IP en estos endpoints y en el login.
- **En desarrollo** el correo no sale: sin `Email:Smtp:Host` los mensajes se guardan en memoria (los ultimos 50) y se leen en `GET http://localhost:8080/identity/dev/emails` para seguir los enlaces a mano. **Nunca van al log ni a la telemetria** (traen tokens); solo el asunto se loguea. Ese endpoint existe unicamente en `Development`.

## Usuarios de desarrollo
**Solo para desarrollo local.** Los siembra el servicio de identidad al arrancar, unicamente en `Development` y con `Identity:SeedDevUsers=true` (lo pone el AppHost). Las contraseñas son triviales a proposito y el repositorio es publico: no las reutilices en ningun lado.
Tienen los mismos ids, alias y claves que tenia el realm de Keycloak. Cada uno tiene el email `<alias>@casino.test` (verificado).

| Usuario | Contraseña | Rol |
| --- | --- | --- |
| `jugador1` | `jugador1-dev` | player |
| `jugador2` | `jugador2-dev` | player |
| `backoffice1` | `backoffice1-dev` | player + backoffice (administrador) |
| `jefe1` | `jefe1-dev` | player + head_cashier (jefe de cajeros) |
| `cajero1` | `cajero1-dev` | player + cashier (cajero) |

Cualquiera puede ademas crear una cuenta desde la pantalla de entrada y recibe el rol `player`. Para pruebas de carga, `Identity:SeedLoadUsers=N` siembra `load-001..N` (ver [pruebas-de-carga.md](pruebas-de-carga.md)).

## Como lo usa el front (React)
- Cliente propio (`web/src/auth/identity.ts`): entra con **alias o email + contraseña** (`grant_type=password` del cliente publico `casino-web`, sin secreto) y renueva con `refresh_token` (con rotacion). El access token dura 10 minutos; el refresh, 14 dias.
- La sesion vive en `sessionStorage` (se pierde al cerrar la pestaña). Un 401 de la API cierra la sesion y vuelve la pantalla de entrada. Anotado en `docs/mejoras.md`: pasar el refresh a una cookie `HttpOnly`.
- Autoridad (issuer): `http://localhost:8080`. Tras el login, llamar a `GET /me`: da de alta al jugador y devuelve su `userId`, su alias y sus roles.

## Probar la API a mano
Se pide un token con el mismo cliente que usa el front (alias o email + contraseña):

```
curl -s -d "grant_type=password" -d "client_id=casino-web" -d "username=jugador1" -d "password=jugador1-dev" \
  http://localhost:8080/connect/token
```

Luego `curl -H "Authorization: Bearer <access_token>" http://localhost:5188/me`.

## Endpoints
| Endpoint | Rol | Descripcion |
| --- | --- | --- |
| `GET /me` | player | Datos del usuario autenticado (lo da de alta si es nuevo) |
| `GET /wallet/me`, `GET /wallet/me/balance?asOf=` | player | Mi cuenta y mi saldo a una fecha |
| `GET /fairness/me`, `POST /fairness/me/rotate` | player | Mis seeds (compromiso) y rotacion |
| `POST /games/roulette/bets` (`Idempotency-Key`) | player | Apostar; el servidor asigna el nonce |
| `GET /games/roulette/rounds`, `GET /games/roulette/rounds/{id}` | player | Mi historial y una ronda propia |
| `GET /backoffice/users` | backoffice | Jugadores registrados (id y fecha de alta) para el panel `/admin` |
| `GET /identity/admin/users?q=`, `PUT /identity/admin/users/{id}/roles` | backoffice | **Servicio de identidad** (no el gateway): cuentas por alias (nunca el email) y sus roles; al cambiar roles se cierran las sesiones de esa cuenta |
| `GET /backoffice/wallet/users/{id}`, `.../balance`, `POST .../credit` | backoffice | Consulta y ajuste manual (cada carga queda en la auditoria) |
| `GET /wallet/me/movements?limit=&before=` | player | Mi extracto: cada cambio de fichas disponibles con el saldo resultante (paginado por cursor) |
| `GET /backoffice/wallet/credits?limit=&before=&userId=&from=&to=` | backoffice | Historial general de cargas con filtros, paginado y total de fichas del filtro |
| `GET /backoffice/games/slots/settings`, `POST .../settings/preview`, `POST .../settings`, `GET .../settings/history` | backoffice | Ajustes de la tragamonedas: ver, probar sin guardar, publicar una version nueva y su historial |
| `GET /cashier/me`, `GET /cashier/members`, `GET /cashier/transfers` | cashier / head_cashier | Mi lugar en la jerarquia, la gente de mi jurisdiccion con su saldo y mis ultimas cargas |
| `POST /cashier/transfers` (`Idempotency-Key`) | cashier / head_cashier | Cargar fichas **desde mi saldo** a alguien de mi jurisdiccion directa (jefe → cajero, cajero → jugador) |
| `GET /backoffice/wallet/hierarchy`, `PUT /backoffice/wallet/hierarchy/{id}` | backoffice | Ver y armar el arbol de cargas (nivel y padre de cada persona) |
| `GET /backoffice/wallet/audit?limit=` | backoffice | Registro de auditoria: quien cargo fichas, a quien, cuanto y cuando |

## Cajeros y jefes de cajeros
Dos roles mas ([ADR 0014](adr/0014-cajeros-jerarquia-y-cobros.md)): **`cashier`** (cajero) y **`head_cashier`** (jefe de cajeros). Los dos conservan `player` (tienen su cuenta de fichas).
- **Los roles de la cuenta dan acceso a los endpoints; el arbol decide a quien se le puede cargar.** El arbol lo arma el backoffice en `/admin/jerarquia`
  (`PUT /backoffice/wallet/hierarchy/{id}`): el jefe no tiene padre, el padre de un cajero es un jefe y el de un jugador es un cajero. Hay que dar **las dos cosas**: el rol (en `/admin/roles`) y el lugar en el arbol;
  el nivel del arbol tiene que coincidir con el rol.
- **Las fichas salen del saldo de quien carga** (transferencia, nunca emision). El backoffice le acredita fichas al jefe (`/backoffice/wallet/users/{id}/credit`); el jefe se las carga a sus cajeros y estos a sus jugadores.
  El jefe solo puede cargar a **sus cajeros** y el cajero solo a **sus jugadores**; cualquier otro destino responde 403.
- Pantalla del cajero: `/cajero` (aparece en el menu para quien tenga el rol), con los **retiros pendientes** de su gente (Cobrar / Rechazar).
- **Comision:** quien carga cobra un porcentaje de lo que carga, **pagado por la casa** (fichas nuevas, acotadas): **2 % para el cajero**, 0 % para el jefe por defecto, con tope por carga (1.000) y por dia UTC (10.000).
  Se configura sin recompilar en la seccion `Cashiers` (`CashierCommissionPermille`, `HeadCashierCommissionPermille`, `MaxCommissionPerLoad`, `MaxCommissionPerDay`). Es hacia abajo (entera) y una carga muy chica puede no pagar nada.
  El jugador recibe **todo** lo cargado; la comision es aparte y aparece en el extracto del cajero como "Comision por cargas". El backoffice ve lo emitido en `GET /backoffice/wallet/commissions`.
- **Nombres:** el cajero ve el **nombre de usuario** (`preferred_username`) de **su** gente para distinguirla, y de nadie mas. El nombre se guarda solo de quienes estan en el arbol (en su nodo) y se renueva cuando esa persona usa la API; hasta entonces
  se ve el id abreviado. Una carga a alguien que ya no esta a cargo del cajero deja de mostrar su nombre. Es el alias (publico): no es el email, que nunca sale del servicio de identidad. De todos modos no va a logs, trazas ni metricas.
- Cada carga queda en la auditoria (`ChipsTransferred`: quien, a quien, cuanto, cuando; solo ids) y en el extracto de los dos como "Fichas recibidas" / "Fichas enviadas".
- **Retiros:** cualquier jugador, desde `/retirar`, pide retirar fichas (minimo 10): quedan **apartadas** hasta que su cajero las cobra (pasan a la cuenta del cajero), las rechaza, el jugador cancela o vence el plazo (72 h) y vuelven. Un jugador sin cajero
  o un jefe de cajeros lo atiende el backoffice en `/admin/retiros` (al cobrar, las fichas vuelven a la casa). Endpoints: `POST|GET /wallet/withdrawals`, `POST /wallet/withdrawals/{id}/cancel`, `GET /cashier/withdrawals`, `POST /cashier/withdrawals/{id}/pay|reject`,
  `GET /backoffice/wallet/withdrawals`, `POST /backoffice/wallet/withdrawals/{id}/pay|reject`. En el extracto aparecen como "Retiro solicitado" y "Retiro devuelto".
- **Entorno local ya levantado:** `python scripts/dev-jerarquia.py` ubica a `jefe1` → `cajero1` → `jugador1`/`jugador2` en el arbol y le acredita fichas al jefe (solo desarrollo; reinicia la app antes si es de una version anterior).
- Para dar el rol a una cuenta que ya existe: `/admin/roles` (o `PUT /identity/admin/users/{id}/roles`).

## Panel de administracion (`/admin`)
El rol `backoffice` es el de administrador: ve el enlace **Admin** en el menu y puede cargar fichas a cualquier jugador (cada carga lleva
`Idempotency-Key`). El rol se da en `/admin/roles` (o con `PUT /identity/admin/users/{id}/roles`); el primer administrador sale de la configuracion `Identity:AdminAliases` (el AppHost la deja en `backoffice1`). El usuario conserva `player`, asi que puede seguir jugando.

### Auditoria de las cargas
Cada carga manual guarda una anotacion (`AuditEntry`, esquema de la Wallet): administrador, jugador, monto, `Idempotency-Key`, id de la
transaccion y fecha. Solo ids, nunca nombre ni email. Es de solo-agregar y esta protegida por la misma idempotencia que la carga: repetir
la misma carga no duplica la anotacion, y si fallo entre la carga y su anotacion, reintentar con la misma clave completa la que faltaba.
Las cargas rechazadas (monto invalido, cuenta inexistente) no dejan anotacion. La doble aprobacion llega en la fase 5.
