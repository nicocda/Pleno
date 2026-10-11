using Casino.Hosting;
using Casino.Hosts.Games;
using Casino.Modules.Games.Api;
using Casino.Modules.Games.Application;
using Casino.Modules.Games.Infrastructure;
using Casino.Modules.Games.Platform;

// Servicio de juegos: la plataforma (equidad provably fair, catalogo y protocolo de rondas con la Wallet) y los juegos que tiene habilitados.
// Tiene su propia base de datos (gamesdb). Cada juego es un modulo (IGameModule) con su propio proyecto: para sumar uno se agrega a AvailableGames
// y para sacarlo se quita (o se deja afuera con Games:Enabled, sin recompilar). Nadie mas lee ni escribe sus tablas.
var builder = WebApplication.CreateBuilder(args);

var games = GameSelection.Select(AvailableGames.All(), builder.Configuration.GetSection("Games:Enabled").Get<string[]>());

builder.AddServiceDefaults();
// Valida el token por su cuenta (no confia en que el gateway ya lo hizo).
builder.AddCasinoAuthentication();
builder.AddCasinoJson();
builder.AddCasinoData("gamesdb", "games", options => GamesMartenConfiguration.Register(options, games));
builder.AddCasinoMessaging("games", [typeof(WalletEventsHandler).Assembly], GamesMessaging.Configure);

builder.Services.AddGamesModule(builder.Configuration, games);

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapGet("/", () => "Casino Games Service");

app.UseAuthentication();
app.UseAuthorization();

app.MapGamesModule(games);

app.Run();
