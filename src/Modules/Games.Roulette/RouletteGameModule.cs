using Casino.Modules.Games.Api;
using Casino.Modules.Games.Application;
using Casino.Modules.Games.Platform;
using Marten;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Casino.Modules.Games.Roulette;

/// <summary>La ruleta europea como modulo de la plataforma de juegos.</summary>
public sealed class RouletteGameModule : IGameModule
{
    public GameInfo Info { get; } = new(
        "roulette", "Ruleta europea", "37 casilleros, 15 tipos de apuesta. Cada tirada se puede verificar.", "/ruleta", "◎");

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<RouletteService>();
        services.AddSingleton<IGameRounds>(sp => sp.GetRequiredService<RouletteService>());
    }

    public void ConfigureStorage(StoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Dos manejadores que procesen el mismo mensaje a la vez no pueden pisarse: el segundo falla y reintenta.
        options.Schema.For<RouletteRound>().Identity(r => r.Id).UseOptimisticConcurrency(true).Index(r => r.UserId);
    }

    public void MapEndpoints(IEndpointRouteBuilder app) => RouletteEndpoints.Map(app);
}
