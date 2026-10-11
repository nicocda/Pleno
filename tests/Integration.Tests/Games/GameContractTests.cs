using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Casino.Hosts.Games;
using Casino.Integration.Tests.Wallet;
using Casino.Modules.Games.Platform;
using Casino.Modules.Games.Roulette;
using Casino.Modules.Games.Slots;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Casino.Integration.Tests.Games;

/// <summary>
/// El contrato base de un juego: lo que la plataforma le pide a cualquiera (una ficha de catalogo, el protocolo de rondas con la Wallet si
/// mueve fichas, endpoints autenticados bajo su propia ruta) y que agregar o quitar un juego sea una cuestion de configuracion.
/// </summary>
[Collection(WalletDbDefinition.Name)]
public sealed class GameContractTests(PostgresFixture db, RabbitMqFixture rabbit) : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    private static IGameModule[] AllGames => AvailableGames.All();

    private static HttpClient WithToken(HttpClient client, params string[] roles)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token(Guid.NewGuid(), roles: roles.Length == 0 ? null : roles));
        return client;
    }

    [Fact]
    public async Task The_catalog_lists_every_enabled_game_with_its_card_and_needs_a_player_token()
    {
        using var cluster = TestAuth.StartApp(db, rabbit);
        _disposables.Add(cluster);
        using var player = cluster.ClientFor(Guid.NewGuid());

        var catalog = await player.GetFromJsonAsync<JsonElement>("/games");

        var cards = catalog.EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!);
        Assert.Equal(["blackjack", "crash", "poker", "roulette", "slots", "truco", "uno"], cards.Keys.Order());
        Assert.Equal("/poker", cards["poker"].GetProperty("route").GetString());
        Assert.Equal("/truco", cards["truco"].GetProperty("route").GetString());
        Assert.Equal("/uno", cards["uno"].GetProperty("route").GetString());
        Assert.Equal("/blackjack", cards["blackjack"].GetProperty("route").GetString());
        Assert.Equal("/crash", cards["crash"].GetProperty("route").GetString());
        Assert.Equal("/ruleta", cards["roulette"].GetProperty("route").GetString());
        Assert.Equal("/tragamonedas", cards["slots"].GetProperty("route").GetString());
        Assert.All(cards.Values, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.GetProperty("name").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(c.GetProperty("tagline").GetString()));
            Assert.Equal("Server", c.GetProperty("resolution").GetString()); // en los juegos actuales decide el servidor
        });
        using var anonymous = cluster.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/games")).StatusCode);
    }

    [Fact]
    public void Every_game_follows_the_conventions_of_the_contract()
    {
        foreach (var game in AllGames)
        {
            // 1) Una ficha valida y estable.
            Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", game.Info.Id);
            Assert.False(string.IsNullOrWhiteSpace(game.Info.Name));
            Assert.StartsWith("/", game.Info.Route);

            // 2) Si el servidor decide el resultado, el juego sigue el protocolo de rondas con la Wallet (registra su IGameRounds).
            var services = new ServiceCollection();
            game.ConfigureServices(services, new ConfigurationBuilder().Build());
            if (game.Info.Resolution == GameResolution.Server)
            {
                Assert.Single(services, d => d.ServiceType == typeof(IGameRounds));
            }

            // 3) Sus endpoints viven bajo su propia ruta (/games/{id}/... o /backoffice/games/{id}/...) y exigen autenticacion.
            var builder = WebApplication.CreateBuilder();
            builder.Host.UseDefaultServiceProvider(options => options.ValidateOnBuild = false); // solo se mapean los endpoints: no hace falta una base de datos
            builder.Services.AddAuthentication();
            builder.Services.AddAuthorization();
            game.ConfigureServices(builder.Services, builder.Configuration); // para que el mapeo pueda inferir los parametros de los endpoints
            var app = builder.Build();
            game.MapEndpoints(app);
            var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToList();
            Assert.NotEmpty(endpoints);
            Assert.All(endpoints, e =>
            {
                var path = e.RoutePattern.RawText!;
                Assert.True(
                    path.StartsWith($"/games/{game.Info.Id}", StringComparison.Ordinal) || path.StartsWith($"/backoffice/games/{game.Info.Id}", StringComparison.Ordinal),
                    $"{game.Info.Id}: {path} esta fuera de la ruta del juego");
                Assert.NotEmpty(e.Metadata.OfType<Microsoft.AspNetCore.Authorization.IAuthorizeData>());
            });
        }
    }

    [Fact]
    public async Task A_game_left_out_of_the_configuration_is_gone_from_the_catalog_and_its_routes()
    {
        using var games = TestAuth.StartGamesHost(db, rabbit, customize: b => b.UseSetting("Games:Enabled:0", "roulette"));
        _disposables.Add(games);
        using var player = WithToken(games.CreateClient());

        var catalog = await player.GetFromJsonAsync<JsonElement>("/games");

        Assert.Equal(["roulette"], catalog.EnumerateArray().Select(c => c.GetProperty("id").GetString()));
        Assert.Equal(HttpStatusCode.OK, (await player.GetAsync("/games/roulette/rounds")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await player.GetAsync("/games/slots/paytable")).StatusCode);
    }

    [Fact]
    public async Task Enabling_a_game_that_does_not_exist_stops_the_service_at_startup()
    {
        using var games = TestAuth.StartGamesHost(db, rabbit, customize: b => b.UseSetting("Games:Enabled:0", "no-existe"));
        _disposables.Add(games);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => games.CreateClient()));

        Assert.Contains("no-existe", ex.ToString());
    }

    [Fact]
    public async Task With_only_one_game_loaded_the_wallet_events_of_the_other_are_simply_ignored()
    {
        // El servicio solo tiene la ruleta: un hecho de la Wallet de una apuesta que no es suya no rompe nada.
        using var games = TestAuth.StartGamesHost(db, rabbit, customize: b => b.UseSetting("Games:Enabled:0", "roulette"));
        _disposables.Add(games);
        var rounds = games.Services.GetServices<IGameRounds>().ToList();

        Assert.Single(rounds);
        await rounds[0].OnStakeReservedAsync(new Casino.Contracts.StakeReserved(Guid.NewGuid(), Guid.NewGuid(), 10, Guid.NewGuid()));
        await rounds[0].OnStakeSettledAsync(new Casino.Contracts.StakeSettled(Guid.NewGuid(), Guid.NewGuid(), 10, 0, Guid.NewGuid()));
    }
}
