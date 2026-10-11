using System.Numerics;

namespace Casino.Modules.Games.Slots;

/// <summary>Un simbolo del rodillo: cuantas veces aparece en la cinta (peso) y cuanto paga por apuesta si salen tres iguales.</summary>
public sealed record SlotSymbol(string Name, int Weight, long TriplePayout);

/// <summary>Premio chico por una racha inicial: los primeros <paramref name="Count"/> rodillos (desde la izquierda) muestran <paramref name="Symbol"/>.</summary>
public sealed record LeadingPay(string Symbol, int Count, long Payout);

/// <summary>
/// Tabla de pagos de una tragamonedas de tres rodillos con cascadas. Cada rodillo es independiente y elige un simbolo con
/// probabilidad proporcional a su peso. Los pagos son multiplicadores ENTEROS de la apuesta, asi que el pago siempre es un entero.
/// <para>
/// Cascadas: un premio que supera la apuesta (paga al menos <see cref="CascadeMinPay"/>) hace "explotar" los rodillos que lo forman:
/// se sortean de nuevo y el premio de la nueva combinacion se multiplica por el siguiente valor de <see cref="CascadeMultipliers"/>.
/// La cadena sigue mientras haya premios que superen la apuesta y se corta sola al agotarse los multiplicadores, asi que el pago
/// maximo tiene tope.
/// </para>
/// El retorno al jugador (RTP) se calcula EXACTO con enteros (recorriendo todas las cadenas posibles) y NUNCA puede superar el 100%:
/// la casa no puede perder a la larga.
/// </summary>
public sealed class SlotsPaytable
{
    public const int ReelCount = 3;

    /// <summary>Un premio dispara la cascada solo si paga al menos esto (es decir, si devuelve mas que la apuesta: un "recupero" no encadena).</summary>
    public const long CascadeMinPay = 2;

    private const int MaxSymbols = 12;
    private const long MaxMultiplier = 100_000;

    private readonly Dictionary<(string Symbol, int Count), long> _leading;

    public SlotsPaytable(IEnumerable<SlotSymbol> symbols, IEnumerable<LeadingPay> leadingPays, long maxStake)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(leadingPays);

        Symbols = [.. symbols];
        LeadingPays = [.. leadingPays];
        MaxStake = maxStake;

        if (Symbols.Count is < 2 or > MaxSymbols)
        {
            throw new ArgumentException($"La tabla necesita entre 2 y {MaxSymbols} simbolos.", nameof(symbols));
        }

        if (Symbols.Any(s => string.IsNullOrWhiteSpace(s.Name))
            || Symbols.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Symbols.Count)
        {
            throw new ArgumentException("Los simbolos necesitan un nombre y no pueden repetirse.", nameof(symbols));
        }

        if (Symbols.Any(s => s.Weight < 1 || s.TriplePayout < 0 || s.TriplePayout > MaxMultiplier))
        {
            throw new ArgumentException($"El peso es al menos 1 y los pagos van de 0 a {MaxMultiplier}.", nameof(symbols));
        }

        _leading = [];
        foreach (var pay in LeadingPays)
        {
            var index = IndexOf(pay.Symbol);
            if (pay.Count is < 1 or >= ReelCount
                || pay.Payout is < 0 or > MaxMultiplier
                || index < 0
                || !_leading.TryAdd((Symbols[index].Name, pay.Count), pay.Payout))
            {
                throw new ArgumentException($"Premio de racha invalido: {pay.Symbol} x{pay.Count}.", nameof(leadingPays));
            }
        }

        if (maxStake is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxStake), "La apuesta maxima va de 1 a 1.000.000 de fichas.");
        }

        TotalWeight = Symbols.Sum(s => s.Weight);
        Denominator = BigInteger.Pow(TotalWeight, ReelCount * CascadeMultipliers.Count);
        ExpectedPayoutUnits = ComputeExpectedPayoutUnits();

        if (ExpectedPayoutUnits <= 0 || ExpectedPayoutUnits > Denominator)
        {
            throw new ArgumentException(
                $"El retorno al jugador debe ser mayor que 0 y no superar el 100% (es {ReturnToPlayerPercent:F2}%, con las cascadas): con mas, la casa perderia a la larga.",
                nameof(symbols));
        }
    }

    /// <summary>
    /// Multiplicador de cada paso de la cascada: el giro inicial vale x1, la primera cascada x2, y asi. El ultimo paso corta la cadena.
    /// Fijo en el codigo (no es configurable): un cambio altera el retorno de TODAS las tablas, asi que se versiona como un cambio de reglas.
    /// </summary>
    public static IReadOnlyList<int> CascadeMultipliers { get; } = [1, 2, 3, 5, 10];

    public IReadOnlyList<SlotSymbol> Symbols { get; }

    public IReadOnlyList<LeadingPay> LeadingPays { get; }

    public long MinStake { get; } = 1;

    public long MaxStake { get; }

    /// <summary>Suma de los pesos de un rodillo: cuantas "paradas" tiene la cinta.</summary>
    public int TotalWeight { get; }

    /// <summary>Cantidad total de combinaciones ponderadas de la cadena mas larga posible (paradas elevado a rodillos por pasos).</summary>
    public BigInteger Denominator { get; }

    /// <summary>Suma, sobre todas las cadenas ponderadas, del multiplicador que pagan. RTP = esto / <see cref="Denominator"/>.</summary>
    public BigInteger ExpectedPayoutUnits { get; }

    /// <summary>Retorno al jugador en porcentaje (solo para mostrar: los calculos usan enteros).</summary>
    public double ReturnToPlayerPercent => (double)(ExpectedPayoutUnits * 10_000_000_000_000 / Denominator) / 100_000_000_000.0;

    /// <summary>Probabilidad (en porcentaje) de que el giro inicial pague algo (las cascadas solo suman premio encima).</summary>
    public double HitRatePercent => 100.0 * CountWinningUnits() / ((long)TotalWeight * TotalWeight * TotalWeight);

    /// <summary>Tabla por defecto: retorno de ~96,6% (cascadas incluidas) y premio mayor de x60 en el giro inicial. Se puede reemplazar desde la configuracion (seccion Slots).</summary>
    public static SlotsPaytable Default { get; } = new(
        [
            new SlotSymbol("Cereza", 20, 4),
            new SlotSymbol("Limon", 16, 6),
            new SlotSymbol("Naranja", 12, 8),
            new SlotSymbol("Campana", 8, 14),
            new SlotSymbol("Bar", 5, 30),
            new SlotSymbol("Siete", 3, 60),
        ],
        [
            new LeadingPay("Cereza", 2, 3),
            new LeadingPay("Cereza", 1, 1),
        ],
        maxStake: 10_000);

    /// <summary>Indice del simbolo que cae en una parada de la cinta (0 a TotalWeight - 1).</summary>
    public int SymbolAtStop(int stop)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(stop);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(stop, TotalWeight);

        var remaining = stop;
        for (var i = 0; i < Symbols.Count; i++)
        {
            remaining -= Symbols[i].Weight;
            if (remaining < 0)
            {
                return i;
            }
        }

        throw new InvalidOperationException("Parada fuera de la cinta.");
    }

    /// <summary>Cuantas veces se multiplica la apuesta para esta combinacion de simbolos (indices por rodillo), sin contar el multiplicador de la cascada.</summary>
    public long MultiplierFor(IReadOnlyList<int> reels)
    {
        ArgumentNullException.ThrowIfNull(reels);
        if (reels.Count != ReelCount || reels.Any(r => r < 0 || r >= Symbols.Count))
        {
            throw new ArgumentException($"Se esperan {ReelCount} rodillos con simbolos validos.", nameof(reels));
        }

        var first = reels[0];
        var run = RunLength(reels);
        return run == ReelCount ? Symbols[first].TriplePayout : _leading.GetValueOrDefault((Symbols[first].Name, run));
    }

    /// <summary>
    /// Cuantos rodillos (desde la izquierda) forman el premio de esta combinacion y vuelven a girar en la cascada;
    /// 0 si no hay premio o si el premio no alcanza para encadenar.
    /// </summary>
    public int CascadingReels(IReadOnlyList<int> reels) => MultiplierFor(reels) < CascadeMinPay ? 0 : RunLength(reels);

    public int IndexOf(string name)
    {
        for (var i = 0; i < Symbols.Count; i++)
        {
            if (string.Equals(Symbols[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static int RunLength(IReadOnlyList<int> reels)
    {
        var run = 1;
        while (run < ReelCount && reels[run] == reels[0])
        {
            run++;
        }

        return run;
    }

    /// <summary>
    /// Esperanza exacta del multiplicador total, recorriendo la cadena de cascadas de atras para adelante.
    /// El valor de un estado en el paso <c>d</c> esta expresado sobre <c>TotalWeight^(ReelCount * (ultimoPaso - d))</c>: asi todo son enteros.
    /// </summary>
    private BigInteger ComputeExpectedPayoutUnits()
    {
        var n = Symbols.Count;
        var last = CascadeMultipliers.Count - 1;
        var states = n * n * n;
        var reels = new int[ReelCount];
        var nextReels = new int[ReelCount];
        BigInteger[] next = [];

        for (var step = last; step >= 0; step--)
        {
            var scale = BigInteger.Pow(TotalWeight, ReelCount * (last - step));
            var current = new BigInteger[states];
            for (var state = 0; state < states; state++)
            {
                Decode(state, reels);
                var pay = MultiplierFor(reels);
                var value = pay * CascadeMultipliers[step] * scale;
                var cascading = step < last ? CascadingReels(reels) : 0;
                if (cascading > 0)
                {
                    // Los rodillos del premio se sortean de nuevo; los demas quedan como estaban.
                    var combos = (int)Math.Pow(n, cascading);
                    for (var combo = 0; combo < combos; combo++)
                    {
                        var weight = BigInteger.Pow(TotalWeight, ReelCount - cascading);
                        var rest = combo;
                        reels.CopyTo(nextReels, 0);
                        for (var r = 0; r < cascading; r++)
                        {
                            var symbol = rest % n;
                            rest /= n;
                            nextReels[r] = symbol;
                            weight *= Symbols[symbol].Weight;
                        }

                        value += weight * next[Encode(nextReels)];
                    }
                }

                current[state] = value;
            }

            next = current;
        }

        BigInteger total = 0;
        for (var state = 0; state < states; state++)
        {
            Decode(state, reels);
            total += (BigInteger)Symbols[reels[0]].Weight * Symbols[reels[1]].Weight * Symbols[reels[2]].Weight * next[state];
        }

        return total;
    }

    private int Encode(int[] reels) => (((reels[0] * Symbols.Count) + reels[1]) * Symbols.Count) + reels[2];

    private void Decode(int state, int[] reels)
    {
        reels[2] = state % Symbols.Count;
        reels[1] = state / Symbols.Count % Symbols.Count;
        reels[0] = state / (Symbols.Count * Symbols.Count);
    }

    private long CountWinningUnits()
    {
        long total = 0;
        var reels = new int[ReelCount];
        for (var state = 0; state < Symbols.Count * Symbols.Count * Symbols.Count; state++)
        {
            Decode(state, reels);
            if (MultiplierFor(reels) > 0)
            {
                total += (long)Symbols[reels[0]].Weight * Symbols[reels[1]].Weight * Symbols[reels[2]].Weight;
            }
        }

        return total;
    }
}
