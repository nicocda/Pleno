using Microsoft.Extensions.Configuration;

namespace Casino.Modules.Games.Poker;

/// <summary>
/// La configuracion del poker (seccion <c>Poker</c>): lo fija la casa, no quien crea la mesa. Los valores por defecto son los del ADR 0015; las opciones de cada mesa (ciega grande,
/// recompras, comision, subida de ciegas) se guardan en ella al crearla, asi cambiar esto no altera las mesas ya creadas.
/// </summary>
public sealed class PokerRules
{
    /// <summary>La comision de inscripcion, en milesimas de la entrada (100 = 10 %). Se paga en cada entrada (la inicial y cada recompra) y no entra al premio.</summary>
    public int EntryFeePermille { get; set; } = 100;

    /// <summary>Cada cuantas manos sube el nivel de ciegas.</summary>
    public int BlindLevelHands { get; set; } = 10;

    /// <summary>Cuanto sube la ciega grande en cada nivel, en porcentaje (50 = x1,5).</summary>
    public int BlindGrowthPercent { get; set; } = 50;

    /// <summary>El rake de la mesa libre, en milesimas del pozo (30 = 3 %). Se cobra de cada pozo que llega al flop, sin tope; lo fija la casa.</summary>
    public int RakePermille { get; set; } = 30;

    /// <summary>Cuantas ciegas grandes tiene que valer la entrada como minimo y como maximo.</summary>
    public int MinEntryInBigBlinds { get; set; } = 20;

    public int MaxEntryInBigBlinds { get; set; } = 100;

    /// <summary>El maximo de recompras que puede tener una mesa.</summary>
    public int MaxRebuys { get; set; } = 2;

    /// <summary>La pausa normal entre una mano y la siguiente.</summary>
    public double BetweenHandsSeconds { get; set; } = 6;

    /// <summary>Cuanto tiene quien se quedo sin fichas para decidir si recompra.</summary>
    public double RebuyWindowSeconds { get; set; } = 20;

    /// <summary>La pausa que queda cuando ya decidieron todos los que podian recomprar.</summary>
    public double AfterDecisionsSeconds { get; set; } = 3;

    public static PokerRules Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var rules = configuration.GetSection("Poker").Get<PokerRules>() ?? new PokerRules();
        rules.Validate();
        return rules;
    }

    public void Validate()
    {
        if (EntryFeePermille is < 0 or > 500)
        {
            throw new InvalidOperationException("Poker:EntryFeePermille tiene que estar entre 0 y 500.");
        }

        if (RakePermille is < 0 or > 200)
        {
            throw new InvalidOperationException("Poker:RakePermille tiene que estar entre 0 y 200.");
        }

        if (BlindLevelHands < 1 || BlindGrowthPercent < 1 || MinEntryInBigBlinds < 1 || MaxEntryInBigBlinds < MinEntryInBigBlinds || MaxRebuys < 0)
        {
            throw new InvalidOperationException("La configuracion de Poker no es valida (niveles, crecimiento, rango de la entrada o recompras).");
        }

        if (BetweenHandsSeconds < 1 || RebuyWindowSeconds < 1 || AfterDecisionsSeconds < 1)
        {
            throw new InvalidOperationException("Las pausas de Poker tienen que ser de al menos 1 segundo.");
        }
    }
}
