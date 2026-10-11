using Casino.Modules.Games.Api;
using Casino.Modules.Games.Application;
using Casino.Modules.Games.Platform;
using Marten;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Casino.Modules.Games.Blackjack;

/// <summary>
/// Blackjack como modulo de la plataforma de juegos: el primer juego de MESAS (ADR 0010). Mesas fijas contra el crupier, varias personas por mesa, turnos,
/// carta tapada del crupier y un zapato provably fair por mano. El modulo trae su propio motor en segundo plano (un ciclo por mesa).
/// </summary>
public sealed class BlackjackGameModule : IGameModule
{
    public GameInfo Info { get; } = new(
        "blackjack", "Blackjack", "Sentate en una mesa, pedi o plantate y llega a 21 sin pasarte. Cada mano se puede verificar.", "/blackjack", "♠");

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = BlackjackOptions.Load(configuration);
        services.AddSingleton(options);
        services.TryAddSingleton<IBlackjackSeedSource, RandomBlackjackSeedSource>();
        services.AddSingleton<BlackjackService>();
        services.AddSingleton<IGameRounds>(sp => sp.GetRequiredService<BlackjackService>());

        // El motor siempre esta registrado (las pruebas corren una mano a la vez); solo arranca solo si esta habilitado.
        services.AddSingleton<BlackjackEngine>();
        if (options.EngineEnabled)
        {
            services.AddHostedService(sp => sp.GetRequiredService<BlackjackEngine>());
        }
    }

    public void ConfigureStorage(StoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // El jugador que actua y el motor que da por terminado el turno no pueden pisarse: el segundo falla y se resuelve.
        options.Schema.For<BlackjackRound>().Identity(r => r.Id).UseOptimisticConcurrency(true).Index(r => r.OpenedAt).Index(r => r.TableId).Index(r => r.Phase);
        options.Schema.For<BlackjackBet>().Identity(b => b.Id).UseOptimisticConcurrency(true).Index(b => b.UserId).Index(b => b.RoundId);
    }

    public void MapEndpoints(IEndpointRouteBuilder app) => BlackjackEndpoints.Map(app);
}
