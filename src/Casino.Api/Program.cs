using Casino.Hosting;
using Casino.Modules.Realtime.Api;
using Casino.Modules.Users.Api;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddCasinoInfrastructure();
// El hub de tiempo real es lo unico que acepta el token en la query (un navegador no puede poner cabeceras en un WebSocket).
builder.AddCasinoAuthentication(PlayerHub.Path);
builder.AddCasinoJson();

// SignalR: si hay Redis, es el "backplane" que reparte los avisos entre todas las instancias de la API.
// Sin el, un aviso generado en una instancia no llegaria a un navegador conectado a otra.
var signalR = builder.Services.AddSignalR();
var redis = builder.Configuration.GetConnectionString("redis");
if (!string.IsNullOrWhiteSpace(redis))
{
    signalR.AddStackExchangeRedis(redis);
}

// CORS: el front corre en otro origen (http://localhost:5173 en desarrollo). Los origenes permitidos son configuracion
// (Cors:AllowedOrigins). Sin configurar no se habilita nada. Se autentica con token, no con cookies: no hace falta AllowCredentials.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
if (allowedOrigins.Length > 0)
{
    builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));
}

// Puerta de entrada (gateway): lo que ya no vive en este proceso se reenvia a su servicio. Las rutas y los destinos son configuracion
// (seccion ReverseProxy); con Aspire, "http://wallet" se resuelve por descubrimiento de servicios. El token viaja tal cual y cada servicio lo valida.
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddServiceDiscoveryDestinationResolver();

builder.Services.AddUsersModule();
builder.Services.AddRealtimeModule();

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapGet("/", () => "Casino API");

// Archivos publicos (wwwroot), por ejemplo la pagina de verificacion de jugadas. No llevan secretos.
app.UseStaticFiles();

if (allowedOrigins.Length > 0)
{
    app.UseCors();
}

app.UseAuthentication();
app.UseAuthorization();
app.UsePlayerProvisioning();

// Todos los endpoints de negocio exigen un token valido y el rol correspondiente (ver cada modulo).
app.MapUsersModule();
app.MapRealtimeModule();
app.MapReverseProxy();

app.Run();

public partial class Program;
