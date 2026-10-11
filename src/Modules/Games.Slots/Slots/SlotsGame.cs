using Casino.Modules.Games.Fairness;

namespace Casino.Modules.Games.Slots;

/// <summary>
/// Un paso del giro: lo que mostraban los rodillos, cuanto paga esa combinacion segun la tabla (<see cref="Pay"/>) y el multiplicador
/// de la cascada (<see cref="Multiplier"/>, x1 en el giro inicial). Lo que gana el paso es <c>Pay * Multiplier</c> veces la apuesta.
/// </summary>
public sealed class SlotsStep
{
    public List<string> Reels { get; set; } = [];

    public long Pay { get; set; }

    public int Multiplier { get; set; }
}

/// <summary>
/// Resultado de un giro: los rodillos iniciales, todos los pasos de la cadena de cascadas (el primero es el giro inicial),
/// cuanto se multiplica la apuesta en total y cuanto se cobra.
/// </summary>
public sealed record SlotsOutcome(IReadOnlyList<string> Reels, IReadOnlyList<SlotsStep> Steps, long Multiplier, long Payout);

public static class SlotsGame
{
    /// <summary>
    /// Resuelve un giro de forma determinista a partir de las seeds y el nonce (el mismo esquema que la ruleta).
    /// Cada rodillo que se sortea (los tres iniciales y, en cada cascada, los que vuelven a girar, de izquierda a derecha)
    /// consume un numero del flujo, asi que el giro completo se puede recalcular y verificar.
    /// </summary>
    public static SlotsOutcome Play(SlotsPaytable paytable, long stake, string serverSeed, string clientSeed, long nonce)
    {
        ArgumentNullException.ThrowIfNull(paytable);
        ArgumentOutOfRangeException.ThrowIfLessThan(stake, 1);

        var rng = new FairRng(serverSeed, clientSeed, nonce);
        var reels = new int[SlotsPaytable.ReelCount];
        for (var i = 0; i < reels.Length; i++)
        {
            reels[i] = paytable.SymbolAtStop(rng.NextInt(paytable.TotalWeight));
        }

        var steps = new List<SlotsStep>();
        long total = 0;
        for (var step = 0; step < SlotsPaytable.CascadeMultipliers.Count; step++)
        {
            var pay = paytable.MultiplierFor(reels);
            var multiplier = SlotsPaytable.CascadeMultipliers[step];
            steps.Add(new SlotsStep { Reels = [.. reels.Select(r => paytable.Symbols[r].Name)], Pay = pay, Multiplier = multiplier });
            total = checked(total + (pay * multiplier));

            var exploding = paytable.CascadingReels(reels);
            if (exploding == 0)
            {
                break;
            }

            for (var i = 0; i < exploding; i++)
            {
                reels[i] = paytable.SymbolAtStop(rng.NextInt(paytable.TotalWeight));
            }
        }

        return new SlotsOutcome(steps[0].Reels, steps, total, checked(stake * total));
    }
}
