var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .WithPgAdmin();
// Una base de datos POR SERVICIO: ningun servicio lee las tablas de otro. Todas viven en el mismo servidor de Postgres (en la nube
// serian servidores o instancias separadas, sin cambiar el codigo).
var walletDb = postgres.AddDatabase("walletdb");
var usersDb = postgres.AddDatabase("usersdb");
var gamesDb = postgres.AddDatabase("gamesdb");
var identityDb = postgres.AddDatabase("identitydb");

var redis = builder.AddRedis("redis")
    .WithDataVolume();

var rabbit = builder.AddRabbitMQ("rabbitmq")
    .WithDataVolume()
    .WithManagementPlugin();

// Servicio de identidad (alias, email cifrado, login, Google, roles y emision de tokens OpenID Connect). Puerto fijo: el emisor (issuer) de los
// tokens incluye la URL, y el front y los servicios tienen que coincidir. Los secretos NO estan en el codigo: son user-secrets (se generan con
// `python scripts/dev-identity-secrets.py`, ver docs/autenticacion.md).
const int IdentityPort = 8080;

// Modo red local (opcional): con `Lan:Host` (la IP de esta PC en la red, ver docs/red-local.md) el front se sirve por HTTPS en esa IP y
// hace de unico punto de entrada: reenvia /api y las rutas de identidad al servicio correspondiente. Asi otros dispositivos solo necesitan llegar al 5173.
// Sin `Lan:Host` todo queda como siempre, en http://localhost.
var lanHost = builder.Configuration["Lan:Host"];
var lanMode = !string.IsNullOrWhiteSpace(lanHost);
var identityLocal = string.Concat("http://localhost:", IdentityPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
var identityMetadata = lanMode ? identityLocal + "/.well-known/openid-configuration" : string.Empty;
var identityAuthority = lanMode ? $"https://{lanHost}:5173" : identityLocal;
var webOrigin = lanMode ? $"https://{lanHost}:5173" : "http://localhost:5173";

var identity = builder.AddProject<Projects.Casino_IdentityService>("identity")
    .WithReference(identityDb).WaitFor(identityDb)
    .WithHttpEndpoint(port: IdentityPort, name: "identity-http", isProxied: false)
    .WithEnvironment("Identity__Issuer", identityAuthority)
    .WithEnvironment("Identity__PublicWebUrl", webOrigin)
    .WithEnvironment("Identity__AllowedReturnOrigins__0", webOrigin)
    .WithEnvironment("Identity__SeedDevUsers", "true")
    .WithEnvironment("Identity__AdminAliases__0", "backoffice1")
    .WithEnvironment("Identity__Email__CurrentKeyVersion", "1")
    .WithEnvironment("Identity__Email__EncryptionKeys__1", builder.AddParameter("identity-email-key", secret: true))
    .WithEnvironment("Identity__Email__HmacKey", builder.AddParameter("identity-email-hmac-key", secret: true));
// Iniciar sesion con Google (opcional). Las credenciales de la app OAuth de Google son secretos: solo user-secrets en local y Key Vault en la nube.
// Sin ellas el boton de Google no aparece y todo sigue igual. Ver docs/autenticacion.md.
//   dotnet user-secrets set "Parameters:google-client-id" <valor> --project src/Casino.AppHost
//   dotnet user-secrets set "Parameters:google-client-secret" <valor> --project src/Casino.AppHost
if (!string.IsNullOrWhiteSpace(builder.Configuration["Parameters:google-client-id"]) && !string.IsNullOrWhiteSpace(builder.Configuration["Parameters:google-client-secret"]))
{
    identity
        .WithEnvironment("Identity__Google__ClientId", builder.AddParameter("google-client-id", secret: true))
        .WithEnvironment("Identity__Google__ClientSecret", builder.AddParameter("google-client-secret", secret: true));
}

// Servicio de la Wallet: dueño de las fichas. No se expone al navegador: se llega a el por el gateway (la API) o por mensajes.
var wallet = builder.AddProject<Projects.Casino_WalletService>("wallet")
    .WithReference(walletDb).WaitFor(walletDb)
    .WithReference(rabbit).WaitFor(rabbit)
    .WithEnvironment("Authentication__Authority", identityAuthority)
    .WithEnvironment("Authentication__MetadataAddress", identityMetadata);

// Servicio de juegos: plataforma provably fair y los juegos habilitados (ruleta, tragamonedas...). Tampoco se expone al navegador.
// La clave maestra de las semillas (Fairness:MasterKey) es un secreto de usuario de ESTE proyecto, no de la API.
var games = builder.AddProject<Projects.Casino_GamesService>("games")
    .WithReference(gamesDb).WaitFor(gamesDb)
    .WithReference(rabbit).WaitFor(rabbit)
    .WithEnvironment("Authentication__Authority", identityAuthority)
    .WithEnvironment("Authentication__MetadataAddress", identityMetadata);

builder.AddProject<Projects.Casino_Api>("api")
    .WithReference(usersDb).WaitFor(usersDb)
    .WithReference(wallet).WaitFor(wallet) // el gateway reenvia /wallet y /backoffice/wallet a este servicio
    .WithReference(games).WaitFor(games) // ... y /games, /fairness y /backoffice/games a este
    .WithReference(redis).WaitFor(redis)
    .WithReference(rabbit).WaitFor(rabbit)
    .WithEnvironment("Authentication__Authority", identityAuthority)
    .WithEnvironment("Authentication__MetadataAddress", identityMetadata)
    .WithEnvironment("Cors__AllowedOrigins__0", webOrigin)
    .WithExternalHttpEndpoints();

// El front (React + Vite). Se levanta con `npm run dev`; las URLs de la API y de Keycloak son las de desarrollo por defecto.
var web = builder.AddViteApp("web", "../../web")
    .WithEndpoint("http", endpoint =>
    {
        endpoint.Port = 5173;
        endpoint.IsProxied = false;
    });
if (lanMode)
{
    // El navegador usa rutas relativas al propio origen; Vite (LAN_MODE) las reenvia a la API y a Keycloak.
    web.WithEnvironment("LAN_MODE", "true")
        .WithEnvironment("VITE_API_URL", "/api")
        .WithEnvironment("VITE_OIDC_AUTHORITY", identityAuthority);
}

builder.Build().Run();
