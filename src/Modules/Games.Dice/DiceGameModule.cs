using Casino.Modules.Games.Dice.Api;
using Casino.Modules.Games.Dice.Application;
using Casino.Modules.Games.Platform;
using Marten;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Casino.Modules.Games.Dice;

/// <summary>Dados (over/under) como modulo de la plataforma de juegos.</summary>
public sealed class DiceGameModule : IGameModule
{
    public GameInfo Info { get; } = new(
        "dice", "Dados", "Elegí tu probabilidad y apostá a que sale por debajo o por encima. Cada tirada se puede verificar.", "/dados", "⚂");

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<DiceService>();
        services.AddSingleton<IGameRounds>(sp => sp.GetRequiredService<DiceService>());
    }

    public void ConfigureStorage(StoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Schema.For<DiceRound>().Identity(r => r.Id).UseOptimisticConcurrency(true).Index(r => r.UserId);
    }

    public void MapEndpoints(IEndpointRouteBuilder app) => DiceEndpoints.Map(app);
}
