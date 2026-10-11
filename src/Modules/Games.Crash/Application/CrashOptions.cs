using Casino.Modules.Games.Crash;
using Microsoft.Extensions.Configuration;

namespace Casino.Modules.Games.Application;

/// <summary>
/// Configuracion de Crash (seccion <c>Crash</c>). Los valores por defecto sirven; se cambian sin recompilar. Se valida al arrancar:
/// un valor absurdo impide iniciar el servicio en vez de dejar un juego roto.
/// </summary>
public sealed class CrashOptions
{
    /// <summary>Cuanto dura la ventana para apostar antes de que el cohete empiece a subir.</summary>
    public double BettingSeconds { get; set; } = 8;

    /// <summary>Pausa entre que explota una ronda y se abre la siguiente.</summary>
    public double PauseSeconds { get; set; } = 4;

    public double GrowthPerSecond { get; set; } = CrashMath.DefaultGrowthPerSecond;

    public int EdgePermille { get; set; } = CrashMath.DefaultEdgePermille;

    public long MinStake { get; set; } = 1;

    public long MaxStake { get; set; } = 10_000;

    /// <summary>Si el motor de rondas corre en este servicio. Hay que dejarlo prendido en UNA sola instancia del servicio de juegos.</summary>
    public bool EngineEnabled { get; set; } = true;

    public static CrashOptions Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection("Crash").Get<CrashOptions>() ?? new CrashOptions();
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (BettingSeconds is < 0.2 or > 120 || PauseSeconds is < 0 or > 120)
        {
            throw new InvalidOperationException("Crash: BettingSeconds va de 0,2 a 120 y PauseSeconds de 0 a 120.");
        }

        if (GrowthPerSecond is < 0.001 or > 20 || EdgePermille is < 0 or > 200)
        {
            throw new InvalidOperationException("Crash: GrowthPerSecond va de 0,001 a 20 y EdgePermille de 0 a 200.");
        }

        if (MinStake < 1 || MaxStake < MinStake || MaxStake > 1_000_000)
        {
            throw new InvalidOperationException("Crash: la apuesta va de 1 a 1.000.000 y el minimo no puede superar al maximo.");
        }
    }
}

/// <summary>
/// De donde sale la semilla de cada ronda. Por defecto es aleatoria criptografica; las pruebas la reemplazan para fijar el resultado
/// (reciben el id de la ronda y la ventaja, que es todo lo que hace falta para saber donde explotaria una semilla).
/// </summary>
public interface ICrashSeedSource
{
    string NextSeed(Guid roundId, int edgePermille);
}

public sealed class RandomCrashSeedSource : ICrashSeedSource
{
    public string NextSeed(Guid roundId, int edgePermille) => Fairness.ProvablyFair.GenerateServerSeed();
}
