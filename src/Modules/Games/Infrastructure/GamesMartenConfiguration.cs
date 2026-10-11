using Casino.Modules.Games.Fairness;
using Casino.Modules.Games.Platform;
using Marten;

namespace Casino.Modules.Games.Infrastructure;

public static class GamesMartenConfiguration
{
    /// <summary>Registra la equidad (comun a todos los juegos) y el almacenamiento de cada juego habilitado en el store del host.</summary>
    public static void Register(StoreOptions options, IEnumerable<IGameModule> games)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(games);

        options.Events.AddEventTypes(
        [
            typeof(SeedPairStarted),
            typeof(NonceAllocated),
            typeof(BetCompleted),
            typeof(SeedPairRotated),
        ]);

        foreach (var game in games)
        {
            game.ConfigureStorage(options);
        }
    }
}
