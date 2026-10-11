using Casino.Modules.Games.Platform;
using System.Runtime.CompilerServices;
using Marten;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Casino.Modules.Games.Tables;

/// <summary>
/// La base de todo juego de mesas ENTRE JUGADORES como modulo de la plataforma. Un juego nuevo solo implementa <see cref="ITableGame"/> (las reglas) y hereda de esta
/// clase pasandole su instancia: la plataforma pone las mesas, las fichas, los turnos, los bots, la semilla, los endpoints y el tiempo real (ADR 0012).
/// </summary>
public abstract class TableGameModule(ITableGame game) : IGameModule
{
    private static readonly ConditionalWeakTable<StoreOptions, object?> Configured = [];

    public GameInfo Info => game.Info;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(game);

        // La plataforma de mesas es una sola para todos los juegos de mesas: se registra la primera vez.
        if (services.Any(d => d.ServiceType == typeof(TableService)))
        {
            return;
        }

        var options = TablesOptions.Load(configuration);
        services.AddSingleton(options);
        services.AddSingleton<TableService>();
        services.AddSingleton<IGameRounds>(sp => sp.GetRequiredService<TableService>());

        // El motor siempre esta registrado (las pruebas lo manejan a mano); solo arranca solo si esta habilitado.
        services.AddSingleton<TableEngine>();
        if (options.EngineEnabled)
        {
            services.AddHostedService(sp => sp.GetRequiredService<TableEngine>());
        }
    }

    public void ConfigureStorage(StoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Varios juegos de mesas comparten estos documentos: se configuran una sola vez (dos veces duplicaria los indices).
        if (!Configured.TryAdd(options, null))
        {
            return;
        }

        // Una jugada del jugador y una del motor (un bot, un turno vencido) sobre la misma mesa no pueden pisarse: la segunda falla, vuelve a leer y reintenta.
        options.Schema.For<PlayerTable>().Identity(t => t.Id).UseOptimisticConcurrency(true).Index(t => t.GameId).Index(t => t.Status).Index(t => t.CreatedAt);
        options.Schema.For<TableBet>().Identity(b => b.Id).UseOptimisticConcurrency(true).Index(b => b.TableId).Index(b => b.UserId);
    }

    public void MapEndpoints(IEndpointRouteBuilder app) => TableEndpoints.Map(app, Info.Id);
}
