using Microsoft.Extensions.Configuration;

namespace Casino.Modules.Games.Application;

/// <summary>Una mesa fija de Blackjack: el casino la define por configuracion.</summary>
public sealed class BlackjackTableConfig
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public long MinStake { get; set; } = 1;

    public long MaxStake { get; set; } = 1_000;
}

/// <summary>
/// Configuracion de Blackjack (seccion <c>Blackjack</c>). Los valores por defecto sirven; se cambian sin recompilar. Se valida al arrancar:
/// un valor absurdo impide iniciar el servicio en vez de dejar un juego roto.
/// </summary>
public sealed class BlackjackOptions
{
    /// <summary>Cuanto dura la ventana de apuestas desde la primera apuesta de la mano.</summary>
    public double BettingSeconds { get; set; } = 7;

    /// <summary>Cuanto tiene cada jugador para decidir en su turno (si no actua, se planta).</summary>
    public double TurnSeconds { get; set; } = 15;

    /// <summary>Pausa entre que termina una mano y se abre la siguiente.</summary>
    public double PauseSeconds { get; set; } = 3;

    /// <summary>Cuanto tarda el crupier entre carta y carta (para que se pueda seguir).</summary>
    public int DealerStepMilliseconds { get; set; } = 500;

    /// <summary>Asientos por mesa.</summary>
    public int MaxSeats { get; set; } = 5;

    /// <summary>Si el motor de mesas corre en este servicio. Hay que dejarlo prendido en UNA sola instancia del servicio de juegos.</summary>
    public bool EngineEnabled { get; set; } = true;

    /// <summary>Las mesas. Si la configuracion no define ninguna se usan las de <see cref="DefaultTables"/>.</summary>
    public List<BlackjackTableConfig> Tables { get; set; } = [];

    public static IReadOnlyList<BlackjackTableConfig> DefaultTables { get; } =
    [
        new() { Id = "mesa-1", Name = "Mesa Principiantes", MinStake = 1, MaxStake = 100 },
        new() { Id = "mesa-2", Name = "Mesa Clasica", MinStake = 10, MaxStake = 1_000 },
        new() { Id = "mesa-3", Name = "Mesa Alta", MinStake = 100, MaxStake = 10_000 },
    ];

    public static BlackjackOptions Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection("Blackjack").Get<BlackjackOptions>() ?? new BlackjackOptions();
        if (options.Tables.Count == 0)
        {
            options.Tables = [.. DefaultTables];
        }

        options.Validate();
        return options;
    }

    public BlackjackTableConfig? FindTable(string tableId) => Tables.Find(t => string.Equals(t.Id, tableId, StringComparison.Ordinal));

    public void Validate()
    {
        if (BettingSeconds is < 0.1 or > 120 || TurnSeconds is < 0.1 or > 120 || PauseSeconds is < 0 or > 120)
        {
            throw new InvalidOperationException("Blackjack: BettingSeconds y TurnSeconds van de 0,1 a 120 y PauseSeconds de 0 a 120.");
        }

        if (DealerStepMilliseconds is < 0 or > 10_000 || MaxSeats is < 1 or > 7)
        {
            throw new InvalidOperationException("Blackjack: DealerStepMilliseconds va de 0 a 10.000 y MaxSeats de 1 a 7.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in Tables)
        {
            if (string.IsNullOrWhiteSpace(table.Id) || table.Id.Length > 40 || table.Id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')) || !ids.Add(table.Id))
            {
                throw new InvalidOperationException("Blackjack: cada mesa necesita un Id unico de hasta 40 letras, numeros o guiones.");
            }

            if (table.MinStake < 1 || table.MaxStake < table.MinStake || table.MaxStake > 1_000_000)
            {
                throw new InvalidOperationException($"Blackjack: la apuesta de la mesa {table.Id} va de 1 a 1.000.000 y el minimo no puede superar al maximo.");
            }
        }
    }
}

/// <summary>
/// De donde sale la semilla de cada mano. Por defecto es aleatoria criptografica; las pruebas la reemplazan para fijar las cartas
/// (reciben el id de la mano y la cantidad de asientos, que es todo lo que hace falta para saber que se repartiria).
/// </summary>
public interface IBlackjackSeedSource
{
    string NextSeed(Guid roundId);
}

public sealed class RandomBlackjackSeedSource : IBlackjackSeedSource
{
    public string NextSeed(Guid roundId) => Fairness.ProvablyFair.GenerateServerSeed();
}
