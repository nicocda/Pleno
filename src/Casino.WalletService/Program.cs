using Casino.Hosting;
using Casino.Modules.Wallet.Api;
using Casino.Modules.Wallet.Application;
using Casino.Modules.Wallet.Infrastructure;

// Servicio de la Wallet: es el unico dueño de las fichas. Tiene su propia base de datos (walletdb), recibe ordenes por la cola
// "wallet.commands" y publica hechos (reserva, liquidacion, cambios de saldo). Nadie mas lee ni escribe sus tablas.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
// Valida el token por su cuenta (no confia en que el gateway ya lo hizo). No hay WebSockets aca: sin rutas con token en la query.
builder.AddCasinoAuthentication();
builder.AddCasinoJson();
builder.AddCasinoData("walletdb", WalletMartenConfiguration.SchemaName, WalletMartenConfiguration.Register);
builder.AddCasinoMessaging("wallet", [typeof(WalletService).Assembly], WalletMessaging.Configure);

builder.Services.AddWalletModule();

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapGet("/", () => "Casino Wallet Service");

app.UseAuthentication();
app.UseAuthorization();

app.MapWalletModule();

app.Run();
