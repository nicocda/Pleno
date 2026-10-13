using Casino.Modules.Identity.Api;

// Servicio de identidad: unico dueño de las cuentas (alias, email cifrado, clave con hash) y de la emision de tokens (OpenID Connect).
// Base propia (identitydb). Los demas servicios solo validan el JWT con sus claves publicas.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddIdentityModule();

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapGet("/", () => "Casino Identity Service");

app.UseIdentityModule();

app.MapIdentityModule();

app.Run();
