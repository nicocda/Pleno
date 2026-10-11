namespace Casino.Modules.Games.Truco;

/// <summary>
/// La baraja espanola de 40 cartas del Truco (sin 8, 9 ni comodines), codificada como un entero de 0 a 39: <c>palo = id / 10</c> (0 espadas, 1 bastos, 2 oros, 3 copas) y
/// <c>id % 10</c> es la posicion del numero en <c>1, 2, 3, 4, 5, 6, 7, 10, 11, 12</c>.
/// </summary>
public static class TrucoCards
{
    public const int DeckSize = 40;

    private static readonly int[] Numbers = [1, 2, 3, 4, 5, 6, 7, 10, 11, 12];

    public static bool IsValid(int card) => card is >= 0 and < DeckSize;

    public static int SuitOf(int card) => card / 10;

    /// <summary>El numero que tiene escrito la carta (1 a 7 y 10 a 12).</summary>
    public static int NumberOf(int card) => Numbers[card % 10];

    public static int CardOf(int suit, int number) => (suit * 10) + Array.IndexOf(Numbers, number);

    /// <summary>
    /// Cuanto vale la carta para ganar una mano (mas alto gana): 1 de espadas, 1 de bastos, 7 de espadas, 7 de oros, los 3, los 2, los 1 falsos (copas y oros), los 12, 11, 10,
    /// los 7 falsos (copas y bastos), 6, 5 y 4.
    /// </summary>
    public static int StrengthOf(int card)
    {
        var suit = SuitOf(card);
        return NumberOf(card) switch
        {
            1 when suit == 0 => 14,
            1 when suit == 1 => 13,
            7 when suit == 0 => 12,
            7 when suit == 2 => 11,
            3 => 10,
            2 => 9,
            1 => 8,
            12 => 7,
            11 => 6,
            10 => 5,
            7 => 4,
            6 => 3,
            5 => 2,
            _ => 1,
        };
    }

    /// <summary>Lo que cuenta la carta para el envido: su numero, y 0 las figuras (10, 11 y 12).</summary>
    public static int EnvidoValueOf(int card)
    {
        var number = NumberOf(card);
        return number >= 10 ? 0 : number;
    }

    /// <summary>
    /// Los puntos de envido de una mano: con dos o mas cartas del mismo palo, 20 mas las dos mejores; sin ningun palo repetido, la carta mas alta.
    /// </summary>
    public static int EnvidoPoints(IReadOnlyCollection<int> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);

        var best = cards.Count == 0 ? 0 : cards.Max(EnvidoValueOf);
        foreach (var suit in cards.GroupBy(SuitOf).Where(g => g.Count() >= 2))
        {
            var top = suit.Select(EnvidoValueOf).OrderByDescending(v => v).Take(2).Sum();
            best = Math.Max(best, 20 + top);
        }

        return best;
    }

    public static List<int> NewDeck() => [.. Enumerable.Range(0, DeckSize)];
}
