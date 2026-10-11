using Casino.Modules.Games.Api;
using Casino.Modules.Games.Application;
using Casino.Modules.Games.Platform;
using Marten;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Casino.Modules.Games.Slots;

/// <summary>La tragamonedas de tres rodillos como modulo de la plataforma de juegos (con su tabla de pagos versionada).</summary>
public sealed class SlotsGameModule : IGameModule
{
    public GameInfo Info { get; } = new(
        "slots", "Tragamonedas", "3 rodillos, premios desde x1 hasta x100 y retorno publicado.", "/tragamonedas", "♣");

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(sp => new SlotsSettingsStore(
            sp.GetRequiredService<IDocumentStore>(),
            SlotsOptions.Load(sp.GetRequiredService<IConfiguration>()),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<SlotsService>();
        services.AddSingleton<IGameRounds>(sp => sp.GetRequiredService<SlotsService>());
    }

    public void ConfigureStorage(StoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Schema.For<SlotsSpin>().Identity(s => s.Id).UseOptimisticConcurrency(true).Index(s => s.UserId);
        options.Schema.For<SlotsSettingsVersion>().Identity(v => v.Id).Index(v => v.Version);
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        SlotsEndpoints.Map(app);
        SlotsAdminEndpoints.Map(app);
    }
}
