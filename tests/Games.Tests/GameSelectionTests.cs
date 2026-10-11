using Casino.Modules.Games.Platform;
using Marten;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Casino.Games.Tests;

/// <summary>Elegir que juegos carga un host: agregar o quitar un juego es una linea de configuracion.</summary>
public class GameSelectionTests
{
    private sealed class FakeGame(string id) : IGameModule
    {
        public GameInfo Info { get; } = new(id, id.ToUpperInvariant(), "descripcion", $"/{id}", "?");

        public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        {
        }

        public void ConfigureStorage(StoreOptions options)
        {
        }

        public void MapEndpoints(IEndpointRouteBuilder app)
        {
        }
    }

    private static readonly IGameModule[] Available = [new FakeGame("roulette"), new FakeGame("slots"), new FakeGame("crash")];

    private static string[] Ids(IEnumerable<IGameModule> games) => [.. games.Select(g => g.Info.Id)];

    [Fact]
    public void Without_configuration_every_available_game_is_loaded()
    {
        Assert.Equal(["roulette", "slots", "crash"], Ids(GameSelection.Select(Available, null)));
        Assert.Equal(["roulette", "slots", "crash"], Ids(GameSelection.Select(Available, [])));
        Assert.Equal(["roulette", "slots", "crash"], Ids(GameSelection.Select(Available, ["", "  "])));
    }

    [Fact]
    public void The_configuration_picks_which_games_are_loaded_and_keeps_the_host_order()
    {
        Assert.Equal(["slots"], Ids(GameSelection.Select(Available, ["slots"])));
        Assert.Equal(["roulette", "crash"], Ids(GameSelection.Select(Available, ["crash", "roulette"]))); // el orden es el del host, no el de la lista
        Assert.Equal(["roulette"], Ids(GameSelection.Select(Available, ["  roulette  "])));
    }

    [Fact]
    public void A_game_that_does_not_exist_stops_the_startup_and_says_which_ones_there_are()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GameSelection.Select(Available, ["roulette", "blackjack"]));

        Assert.Contains("blackjack", ex.Message);
        Assert.Contains("slots", ex.Message);
    }

    [Fact]
    public void Repeated_or_malformed_game_ids_are_refused()
    {
        Assert.Throws<InvalidOperationException>(() => GameSelection.Select([new FakeGame("slots"), new FakeGame("slots")], null));
        Assert.Throws<InvalidOperationException>(() => GameSelection.Select([new FakeGame("Slots")], null));
        Assert.Throws<InvalidOperationException>(() => GameSelection.Select([new FakeGame("mi juego")], null));
        Assert.Throws<InvalidOperationException>(() => GameSelection.Select([new FakeGame("-x")], null));
        Assert.Single(GameSelection.Select([new FakeGame("video-poker-2")], null));
    }

    [Fact]
    public void A_game_card_declares_who_decides_the_result_and_defaults_to_the_server()
    {
        Assert.Equal(GameResolution.Server, new GameInfo("a", "A", "t", "/a", "?").Resolution);
        Assert.Equal(GameResolution.Client, new GameInfo("a", "A", "t", "/a", "?", GameResolution.Client).Resolution);
    }
}
