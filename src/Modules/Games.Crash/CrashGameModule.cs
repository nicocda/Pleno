using Casino.Modules.Games.Api;
using Casino.Modules.Games.Application;
using Casino.Modules.Games.Platform;
using Marten;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Casino.Modules.Games.Crash;

/// <summary>
/// Crash como modulo de la plataforma de juegos: un cohete que sube con un multiplicador y explota en un momento que decide el servidor
/// (provably fair, con un compromiso por ronda). Es el primer juego de ronda COMPARTIDA y en vivo: el modulo trae su propio motor en segundo plano.
/// </summary>
public sealed class CrashGameModule : IGameModule
{
    public GameInfo Info { get; } = new(
        "crash", "Crash", "Un cohete sube y vos decidis cuando retirar antes de que explote. Cada ronda se puede verificar.", "/crash", "▲");

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = CrashOptions.Load(configuration);
        services.AddSingleton(options);
        services.TryAddSingleton<ICrashSeedSource, RandomCrashSeedSource>();
        services.AddSingleton<CrashService>();
        services.AddSingleton<IGameRounds>(sp => sp.GetRequiredService<CrashService>());

        // El motor siempre esta registrado (las pruebas corren una ronda a la vez); solo arranca solo si esta habilitado.
        services.AddSingleton<CrashEngine>();
        if (options.EngineEnabled)
        {
            services.AddHostedService(sp => sp.GetRequiredService<CrashEngine>());
        }
    }

    public void ConfigureStorage(StoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Dos escritores sobre la misma apuesta (el jugador que retira y el motor que explota) no pueden pisarse: el segundo falla y se resuelve.
        options.Schema.For<CrashRound>().Identity(r => r.Id).UseOptimisticConcurrency(true).Index(r => r.OpenedAt).Index(r => r.Phase);
        options.Schema.For<CrashBet>().Identity(b => b.Id).UseOptimisticConcurrency(true).Index(b => b.UserId).Index(b => b.RoundId);
    }

    public void MapEndpoints(IEndpointRouteBuilder app) => CrashEndpoints.Map(app);
}
