var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .WithPgAdmin();
// Una base de datos POR SERVICIO: ningun servicio lee las tablas de otro. Todas viven en el mismo servidor de Postgres (en la nube
// serian servidores o instancias separadas, sin cambiar el codigo).
var walletDb = postgres.AddDatabase("walletdb");
var usersDb = postgres.AddDatabase("usersdb");
var gamesDb = postgres.AddDatabase("gamesdb");

var redis = builder.AddRedis("redis")
    .WithDataVolume();

var rabbit = builder.AddRabbitMQ("rabbitmq")
    .WithDataVolume()
    .WithManagementPlugin();

// Keycloak: servidor de identidad (registro, login y tokens). Puerto fijo: el emisor (issuer) de los tokens incluye la URL,
// y el front y la API tienen que coincidir. La contraseña del administrador NO esta en el codigo: es un secreto de usuario
// (dotnet user-secrets set "Parameters:keycloak-admin-password" <valor> --project src/Casino.AppHost).
const int KeycloakPort = 8080;

// Modo red local (opcional): con `Lan:Host` (la IP de esta PC en la red, ver docs/red-local.md) el front se sirve por HTTPS en esa IP y
// hace de unico punto de entrada: reenvia /api a la API y /realms a Keycloak. Asi otros dispositivos solo necesitan llegar al 5173.
// Sin `Lan:Host` todo queda como siempre, en http://localhost.
var lanHost = builder.Configuration["Lan:Host"];
var lanMode = !string.IsNullOrWhiteSpace(lanHost);
var keycloakLocal = string.Concat("http://localhost:", KeycloakPort.ToString(System.Globalization.CultureInfo.InvariantCulture), "/realms/casino");
var keycloakMetadata = lanMode ? keycloakLocal + "/.well-known/openid-configuration" : string.Empty;
var keycloakAuthority = lanMode ? $"https://{lanHost}:5173/realms/casino" : keycloakLocal;
var keycloakAdminPassword = builder.AddParameter("keycloak-admin-password", secret: true);
var keycloak = builder.AddContainer("keycloak", "quay.io/keycloak/keycloak", "26.7.0")
    .WithArgs("start-dev", "--import-realm")
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", keycloakAdminPassword)
    .WithHttpEndpoint(port: KeycloakPort, targetPort: 8080, name: "http")
    .WithBindMount("./realms", "/opt/keycloak/data/import", isReadOnly: true)
    // El volumen va en /opt/keycloak/data (que existe en la imagen con el dueño correcto), no en una subcarpeta que Docker crearia como root.
    .WithVolume("keycloak-data", "/opt/keycloak/data");
// Iniciar sesion con Google (opcional). Las credenciales de la app OAuth de Google son secretos: solo user-secrets en local y Key Vault en la nube,
// nunca en el realm versionado. Sin ellas el boton de Google no aparece y todo sigue igual. Ver docs/autenticacion.md.
//   dotnet user-secrets set "Parameters:google-client-id" <valor> --project src/Casino.AppHost
//   dotnet user-secrets set "Parameters:google-client-secret" <valor> --project src/Casino.AppHost
if (!string.IsNullOrWhiteSpace(builder.Configuration["Parameters:google-client-id"]) && !string.IsNullOrWhiteSpace(builder.Configuration["Parameters:google-client-secret"]))
{
    keycloak
        .WithEnvironment("GOOGLE_ENABLED", "true")
        .WithEnvironment("GOOGLE_CLIENT_ID", builder.AddParameter("google-client-id", secret: true))
        .WithEnvironment("GOOGLE_CLIENT_SECRET", builder.AddParameter("google-client-secret", secret: true));
}

if (lanMode)
{
    // El emisor de los tokens es la URL publica (la del proxy del front). Las URLs "backchannel" (jwks, token...) se arman con el host
    // de cada pedido, asi los servicios siguen leyendo las claves por http://localhost:8080 sin pasar por el certificado autofirmado.
    keycloak
        .WithEnvironment("KC_HOSTNAME", $"https://{lanHost}:5173")
        .WithEnvironment("KC_HOSTNAME_BACKCHANNEL_DYNAMIC", "true")
        .WithEnvironment("KC_HOSTNAME_ADMIN", "http://localhost:8080") // la consola de administracion sigue en localhost (el proxy del front no la reenvia)
        .WithEnvironment("KC_PROXY_HEADERS", "xforwarded");
}


// Origen del front. Keycloak solo acepta redirecciones a esta URL (ver realms/casino-realm.json), asi que el puerto es fijo.
var webOrigin = lanMode ? $"https://{lanHost}:5173" : "http://localhost:5173";

// Servicio de la Wallet: dueño de las fichas. No se expone al navegador: se llega a el por el gateway (la API) o por mensajes.
var wallet = builder.AddProject<Projects.Casino_WalletService>("wallet")
    .WithReference(walletDb).WaitFor(walletDb)
    .WithReference(rabbit).WaitFor(rabbit)
    .WithEnvironment("Authentication__Authority", keycloakAuthority)
    .WithEnvironment("Authentication__MetadataAddress", keycloakMetadata);

// Servicio de juegos: plataforma provably fair y los juegos habilitados (ruleta, tragamonedas...). Tampoco se expone al navegador.
// La clave maestra de las semillas (Fairness:MasterKey) es un secreto de usuario de ESTE proyecto, no de la API.
var games = builder.AddProject<Projects.Casino_GamesService>("games")
    .WithReference(gamesDb).WaitFor(gamesDb)
    .WithReference(rabbit).WaitFor(rabbit)
    .WithEnvironment("Authentication__Authority", keycloakAuthority)
    .WithEnvironment("Authentication__MetadataAddress", keycloakMetadata);

builder.AddProject<Projects.Casino_Api>("api")
    .WithReference(usersDb).WaitFor(usersDb)
    .WithReference(wallet).WaitFor(wallet) // el gateway reenvia /wallet y /backoffice/wallet a este servicio
    .WithReference(games).WaitFor(games) // ... y /games, /fairness y /backoffice/games a este
    .WithReference(redis).WaitFor(redis)
    .WithReference(rabbit).WaitFor(rabbit)
    .WithEnvironment("Authentication__Authority", keycloakAuthority)
    .WithEnvironment("Authentication__MetadataAddress", keycloakMetadata)
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
        .WithEnvironment("VITE_OIDC_AUTHORITY", keycloakAuthority);
}

builder.Build().Run();
