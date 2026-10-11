# Autenticacion: como usarla en desarrollo

Decision y motivos: [ADR 0003](adr/0003-identidad-con-keycloak.md).

## Levantar todo
`aspire run` (o `aspire start`) levanta Postgres, Redis, RabbitMQ, **Keycloak** y la API. Keycloak queda en
**http://localhost:8080** (consola de administracion: usuario `admin`; su clave es un secreto de usuario, ver abajo).

## Secretos que necesita el entorno local
Ninguno se versiona. Se guardan con user-secrets:

| Secreto | Proyecto | Para que |
| --- | --- | --- |
| `Parameters:keycloak-admin-password` | `src/Casino.AppHost` | Clave del administrador de Keycloak |
| `Fairness:MasterKey` | `src/Casino.GamesService` | Clave maestra que cifra las server seeds (32 bytes en base64) |

```
dotnet user-secrets set "Parameters:keycloak-admin-password" "<valor>" --project src/Casino.AppHost
dotnet user-secrets set "Fairness:MasterKey" "<32 bytes aleatorios en base64>" --project src/Casino.GamesService
```

## Entrar con Google (opcional)
El realm trae el proveedor **Google** ya definido pero apagado. Para encenderlo hace falta una app OAuth propia (las credenciales son secretos):

1. En [Google Cloud Console](https://console.cloud.google.com/apis/credentials) crear una credencial **ID de cliente de OAuth** de tipo *Aplicacion web*.
   Origen autorizado: `http://localhost:8080`. URI de redireccion autorizada: `http://localhost:8080/realms/casino/broker/google/endpoint`.
2. Guardar el ID y el secreto con user-secrets (nunca en el repositorio):
   ```
   dotnet user-secrets set "Parameters:google-client-id" "<id>" --project src/Casino.AppHost
   dotnet user-secrets set "Parameters:google-client-secret" "<secreto>" --project src/Casino.AppHost
   ```
3. Reiniciar con `aspire run`: la pantalla de login muestra **Google** junto al usuario y la contraseña. Quien entra con Google recibe el rol `player` igual que
   quien se registra, y la API ve el mismo token de siempre (la identidad sigue saliendo del token).

Sin esas credenciales el boton no aparece. En la nube las mismas dos credenciales van en Key Vault.

> **Realm ya importado:** Keycloak solo importa el realm la primera vez. Si tu entorno local ya existia, borra el volumen `keycloak-data` (se pierden los usuarios de
> prueba creados a mano) o cargalo en la consola: *Identity providers > Google* con el ID y el secreto.

> **Cuentas repetidas:** si alguien ya se registro con usuario y contraseña y entra con Google con el mismo email, Keycloak le pide confirmar que es suya
> (flujo *first broker login*) en vez de crear otro jugador o dejarle tomar la cuenta. El email de Google es PII: se enmascara en logs como cualquier otro.

## Usuarios de desarrollo del realm `casino`
**Solo para desarrollo local.** Las contraseñas son triviales a proposito y el repositorio es publico: no las reutilices en ningun lado.

| Usuario | Contraseña | Rol |
| --- | --- | --- |
| `jugador1` | `jugador1-dev` | player |
| `jugador2` | `jugador2-dev` | player |
| `backoffice1` | `backoffice1-dev` | backoffice |
| `jefe1` | `jefe1-dev` | player + head_cashier (jefe de cajeros) |
| `cajero1` | `cajero1-dev` | player + cashier (cajero) |

Cualquiera puede ademas registrarse desde la pantalla de login (registro habilitado) y recibe el rol `player`.

## Como lo usa el front (React)
- Flujo **Authorization Code con PKCE**, cliente publico `casino-web` (redirecciones permitidas: `http://localhost:5173/*`).
- Autoridad (issuer): `http://localhost:8080/realms/casino`.
- Cada llamada a la API lleva `Authorization: Bearer <access_token>`. Tras el login, llamar a `GET /me`: da de alta al jugador y
  devuelve su `userId`, su nombre y sus roles.

## Probar la API a mano
Con el cliente de pruebas `casino-tests` (solo desarrollo) se pide un token con usuario y contraseña:

```
curl -s -d "grant_type=password" -d "client_id=casino-tests" -d "username=jugador1" -d "password=jugador1-dev" \
  http://localhost:8080/realms/casino/protocol/openid-connect/token
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
- **Los roles de Keycloak dan acceso a los endpoints; el arbol decide a quien se le puede cargar.** El arbol lo arma el backoffice en `/admin/jerarquia`
  (`PUT /backoffice/wallet/hierarchy/{id}`): el jefe no tiene padre, el padre de un cajero es un jefe y el de un jugador es un cajero. Hay que dar **las dos cosas**: el rol en Keycloak y el lugar en el arbol;
  el nivel del arbol tiene que coincidir con el rol.
- **Las fichas salen del saldo de quien carga** (transferencia, nunca emision). El backoffice le acredita fichas al jefe (`/backoffice/wallet/users/{id}/credit`); el jefe se las carga a sus cajeros y estos a sus jugadores.
  El jefe solo puede cargar a **sus cajeros** y el cajero solo a **sus jugadores**; cualquier otro destino responde 403.
- Pantalla del cajero: `/cajero` (aparece en el menu para quien tenga el rol), con los **retiros pendientes** de su gente (Cobrar / Rechazar).
- **Comision:** quien carga cobra un porcentaje de lo que carga, **pagado por la casa** (fichas nuevas, acotadas): **2 % para el cajero**, 0 % para el jefe por defecto, con tope por carga (1.000) y por dia UTC (10.000).
  Se configura sin recompilar en la seccion `Cashiers` (`CashierCommissionPermille`, `HeadCashierCommissionPermille`, `MaxCommissionPerLoad`, `MaxCommissionPerDay`). Es hacia abajo (entera) y una carga muy chica puede no pagar nada.
  El jugador recibe **todo** lo cargado; la comision es aparte y aparece en el extracto del cajero como "Comision por cargas". El backoffice ve lo emitido en `GET /backoffice/wallet/commissions`.
- **Nombres:** el cajero ve el **nombre de usuario** (`preferred_username`) de **su** gente para distinguirla, y de nadie mas. El nombre se guarda solo de quienes estan en el arbol (en su nodo) y se renueva cuando esa persona usa la API; hasta entonces
  se ve el id abreviado. Una carga a alguien que ya no esta a cargo del cajero deja de mostrar su nombre. Es dato personal: no va a logs, trazas ni metricas. Ojo: con el login de Google el nombre de usuario puede ser el email.
- Cada carga queda en la auditoria (`ChipsTransferred`: quien, a quien, cuanto, cuando; solo ids) y en el extracto de los dos como "Fichas recibidas" / "Fichas enviadas".
- **Retiros:** cualquier jugador, desde `/retirar`, pide retirar fichas (minimo 10): quedan **apartadas** hasta que su cajero las cobra (pasan a la cuenta del cajero), las rechaza, el jugador cancela o vence el plazo (72 h) y vuelven. Un jugador sin cajero
  o un jefe de cajeros lo atiende el backoffice en `/admin/retiros` (al cobrar, las fichas vuelven a la casa). Endpoints: `POST|GET /wallet/withdrawals`, `POST /wallet/withdrawals/{id}/cancel`, `GET /cashier/withdrawals`, `POST /cashier/withdrawals/{id}/pay|reject`,
  `GET /backoffice/wallet/withdrawals`, `POST /backoffice/wallet/withdrawals/{id}/pay|reject`. En el extracto aparecen como "Retiro solicitado" y "Retiro devuelto".
- **Entorno local ya levantado:** `python scripts/dev-jerarquia.py` ubica a `jefe1` → `cajero1` → `jugador1`/`jugador2` en el arbol y le acredita fichas al jefe (solo desarrollo; reinicia la app antes si es de una version anterior).
- Para dar el rol a un usuario que ya existe: consola de Keycloak (Users > usuario > Role mapping > `cashier` o `head_cashier`).

## Panel de administracion (`/admin`)
El rol `backoffice` es el de administrador: ve el enlace **Admin** en el menu y puede cargar fichas a cualquier jugador (cada carga lleva
`Idempotency-Key`). Como Keycloak solo importa el realm la primera vez, para dar el rol a un usuario que ya existe se asigna en la consola
de Keycloak (Users > usuario > Role mapping > `backoffice`) o con la API de administracion. El usuario conserva `player`, asi que puede seguir jugando.

### Auditoria de las cargas
Cada carga manual guarda una anotacion (`AuditEntry`, esquema de la Wallet): administrador, jugador, monto, `Idempotency-Key`, id de la
transaccion y fecha. Solo ids, nunca nombre ni email. Es de solo-agregar y esta protegida por la misma idempotencia que la carga: repetir
la misma carga no duplica la anotacion, y si fallo entre la carga y su anotacion, reintentar con la misma clave completa la que faltaba.
Las cargas rechazadas (monto invalido, cuenta inexistente) no dejan anotacion. La doble aprobacion llega en la fase 5.
