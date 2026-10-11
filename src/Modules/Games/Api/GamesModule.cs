using Casino.BuildingBlocks;
using Casino.Modules.Games.Application;
using Casino.Modules.Games.Infrastructure;
using Casino.Modules.Games.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Casino.Modules.Games.Api;

/// <summary>
/// El nucleo de la plataforma de juegos: la equidad (seeds y nonces, comun a todos), el catalogo y el reparto de los hechos de la Wallet.
/// Cada juego se enchufa como un <see cref="IGameModule"/> y aporta lo suyo.
/// </summary>
public static class GamesModule
{
    /// <summary>Requiere que el host registre antes el IDocumentStore y provea 'Fairness:MasterKey' (user-secrets o Key Vault).</summary>
    public static IServiceCollection AddGamesModule(this IServiceCollection services, IConfiguration configuration, IEnumerable<IGameModule> games)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(games);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => SeedProtector.FromBase64Key(sp.GetRequiredService<IConfiguration>()["Fairness:MasterKey"]));
        services.TryAddSingleton<IOutboxFactory, WolverineOutboxFactory>();
        services.AddSingleton<FairnessService>();

        foreach (var game in games)
        {
            services.AddSingleton(game);
            game.ConfigureServices(services, configuration);
        }

        return services;
    }

    public static IEndpointRouteBuilder MapGamesModule(this IEndpointRouteBuilder app, IEnumerable<IGameModule> games)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(games);

        FairnessEndpoints.Map(app);
        GamesCatalogEndpoints.Map(app);
        foreach (var game in games)
        {
            game.MapEndpoints(app);
        }

        return app;
    }
}
