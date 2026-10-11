using System.Collections.Frozen;

namespace Casino.Modules.Games.Roulette;

public enum RouletteBetType
{
    /// <summary>Pleno: 1 numero.</summary>
    Straight,

    /// <summary>Caballo: 2 numeros adyacentes.</summary>
    Split,

    /// <summary>Calle: una fila de 3.</summary>
    Street,

    /// <summary>Trio: 0-1-2 o 0-2-3.</summary>
    Trio,

    /// <summary>Cuadro: 4 numeros que forman un cuadrado.</summary>
    Corner,

    /// <summary>Primeros cuatro: 0-1-2-3.</summary>
    FirstFour,

    /// <summary>Seisena: dos calles contiguas.</summary>
    SixLine,

    /// <summary>Docena 1, 2 o 3 (seleccion = 1, 2 o 3).</summary>
    Dozen,

    /// <summary>Columna 1, 2 o 3 (seleccion = 1, 2 o 3).</summary>
    Column,

    Red,
    Black,
    Even,
    Odd,

    /// <summary>Falta: 1 a 18.</summary>
    Low,

    /// <summary>Pasa: 19 a 36.</summary>
    High,
}

/// <summary>
/// Apuesta de ruleta europea. Toda apuesta cubre un conjunto de numeros y paga apuesta x 36 / cantidad cubierta:
/// siempre es un entero porque 1, 2, 3, 4, 6, 12 y 18 dividen a 36.
/// </summary>
public sealed class RouletteBet
{
    public const int PocketCount = 37;
    private const int PayoutNumerator = 36;

    private static readonly FrozenSet<int> RedNumbers =
        new[] { 1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36 }.ToFrozenSet();

    private RouletteBet(RouletteBetType type, IReadOnlyList<int> selection, IReadOnlyList<int> covered, long stake)
    {
        Type = type;
        Selection = selection;
        Covered = covered;
        Stake = stake;
    }

    public RouletteBetType Type { get; }

    /// <summary>Lo que eligio el jugador (numeros, o 1-3 para docena y columna).</summary>
    public IReadOnlyList<int> Selection { get; }

    /// <summary>Todos los numeros que hacen ganar la apuesta, ordenados.</summary>
    public IReadOnlyList<int> Covered { get; }

    public long Stake { get; }

    public static bool TryCreate(RouletteBetType type, IEnumerable<int> selection, long stake, out RouletteBet? bet)
    {
        ArgumentNullException.ThrowIfNull(selection);

        bet = null;
        if (stake <= 0)
        {
            return false;
        }

        var chosen = selection.ToArray();
        Array.Sort(chosen);
        if (chosen.Distinct().Count() != chosen.Length)
        {
            return false;
        }

        var covered = CoveredNumbers(type, chosen);
        if (covered is null)
        {
            return false;
        }

        bet = new RouletteBet(type, chosen, covered, stake);
        return true;
    }

    public static RouletteBet Create(RouletteBetType type, IEnumerable<int> selection, long stake)
    {
        var chosen = selection.ToArray();
        return TryCreate(type, chosen, stake, out var bet)
            ? bet!
            : throw new ArgumentException($"Apuesta invalida: {type} con seleccion [{string.Join(",", chosen)}] y apuesta {stake}.");
    }

    public bool Wins(int winningNumber) => Covered.Contains(winningNumber);

    /// <summary>Premio total en fichas (incluye la apuesta devuelta). 0 si pierde.</summary>
    public long PayoutFor(int winningNumber) =>
        Wins(winningNumber) ? checked(Stake * PayoutNumerator / Covered.Count) : 0;

    private static int[]? CoveredNumbers(RouletteBetType type, int[] n) => type switch
    {
        RouletteBetType.Straight => n.Length == 1 && InPocket(n[0]) ? n : null,
        RouletteBetType.Split => IsSplit(n) ? n : null,
        RouletteBetType.Street => IsStreet(n) ? n : null,
        RouletteBetType.Trio => n is [0, 1, 2] or [0, 2, 3] ? n : null,
        RouletteBetType.Corner => IsCorner(n) ? n : null,
        RouletteBetType.FirstFour => n is [0, 1, 2, 3] ? n : null,
        RouletteBetType.SixLine => IsSixLine(n) ? n : null,
        RouletteBetType.Dozen => n is [>= 1 and <= 3] ? Numbers(x => ((x - 1) / 12) == n[0] - 1) : null,
        RouletteBetType.Column => n is [>= 1 and <= 3] ? Numbers(x => ((x - 1) % 3) == n[0] - 1) : null,
        RouletteBetType.Red => n.Length == 0 ? Numbers(RedNumbers.Contains) : null,
        RouletteBetType.Black => n.Length == 0 ? Numbers(x => !RedNumbers.Contains(x)) : null,
        RouletteBetType.Even => n.Length == 0 ? Numbers(x => x % 2 == 0) : null,
        RouletteBetType.Odd => n.Length == 0 ? Numbers(x => x % 2 == 1) : null,
        RouletteBetType.Low => n.Length == 0 ? Numbers(x => x <= 18) : null,
        RouletteBetType.High => n.Length == 0 ? Numbers(x => x >= 19) : null,
        _ => null,
    };

    /// <summary>Numeros 1 a 36 (el cero nunca entra en las apuestas externas) que cumplen el criterio.</summary>
    private static int[] Numbers(Func<int, bool> predicate) => [.. Enumerable.Range(1, 36).Where(predicate)];

    private static bool InPocket(int number) => number is >= 0 and < PocketCount;

    private static bool InGrid(int number) => number is >= 1 and <= 36;

    // El tablero tiene 12 filas (calles) de 3 columnas: la fila de n es (n-1)/3 y su columna (n-1)%3.
    private static int Row(int number) => (number - 1) / 3;

    private static int Col(int number) => (number - 1) % 3;

    private static bool IsSplit(int[] n)
    {
        if (n.Length != 2 || !InPocket(n[0]) || !InPocket(n[1]))
        {
            return false;
        }

        var (a, b) = (n[0], n[1]);
        if (a == 0)
        {
            return b is 1 or 2 or 3;
        }

        return b - a == 3 || (b - a == 1 && Row(a) == Row(b));
    }

    private static bool IsStreet(int[] n) =>
        n.Length == 3 && InGrid(n[0]) && Col(n[0]) == 0 && n[1] == n[0] + 1 && n[2] == n[0] + 2;

    private static bool IsCorner(int[] n) =>
        n.Length == 4 && InGrid(n[0]) && Col(n[0]) < 2 && n[1] == n[0] + 1 && n[2] == n[0] + 3 && n[3] == n[0] + 4 && n[3] <= 36;

    private static bool IsSixLine(int[] n) =>
        n.Length == 6 && InGrid(n[0]) && Col(n[0]) == 0 && n[5] <= 36 && n.Zip(Enumerable.Range(n[0], 6)).All(p => p.First == p.Second);
}
