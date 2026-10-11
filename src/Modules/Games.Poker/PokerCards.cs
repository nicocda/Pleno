namespace Casino.Modules.Games.Poker;

/// <summary>El tipo de mano de poker, de menor a mayor.</summary>
public enum PokerCategory
{
    HighCard = 0,
    Pair = 1,
    TwoPair = 2,
    ThreeOfAKind = 3,
    Straight = 4,
    Flush = 5,
    FullHouse = 6,
    FourOfAKind = 7,
    StraightFlush = 8,
}

/// <summary>
/// La baraja francesa de 52 cartas, codificada como un entero de 0 a 51: <c>palo = id / 13</c> (0 picas, 1 corazones, 2 diamantes, 3 treboles) y <c>id % 13</c> es el rango
/// (0 = 2, 1 = 3 ... 8 = 10, 9 = J, 10 = Q, 11 = K, 12 = As). Tambien evalua manos: la mejor de 5 cartas entre hasta 7, como un puntaje que se compara con un entero.
/// </summary>
public static class PokerCards
{
    public const int DeckSize = 52;

    public static bool IsValid(int card) => card is >= 0 and < DeckSize;

    public static int SuitOf(int card) => card / 13;

    /// <summary>El rango de 2 a 14 (el as es 14).</summary>
    public static int RankOf(int card) => (card % 13) + 2;

    public static List<int> NewDeck() => [.. Enumerable.Range(0, DeckSize)];

    /// <summary>
    /// El puntaje de la mejor mano de 5 cartas que se puede armar con estas (de 5 a 7). Un puntaje mayor gana; iguales empatan. Se arma como
    /// <c>categoria &lt;&lt; 20 | cinco rangos de 4 bits</c> (los que desempatan, en orden de importancia).
    /// </summary>
    public static long Evaluate(IReadOnlyList<int> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        if (cards.Count is < 5 or > 7)
        {
            throw new ArgumentException("Hacen falta entre 5 y 7 cartas.", nameof(cards));
        }

        var best = long.MinValue;
        Span<int> pick = stackalloc int[5];
        for (var a = 0; a < cards.Count - 4; a++)
        {
            for (var b = a + 1; b < cards.Count - 3; b++)
            {
                for (var c = b + 1; c < cards.Count - 2; c++)
                {
                    for (var d = c + 1; d < cards.Count - 1; d++)
                    {
                        for (var e = d + 1; e < cards.Count; e++)
                        {
                            pick[0] = cards[a];
                            pick[1] = cards[b];
                            pick[2] = cards[c];
                            pick[3] = cards[d];
                            pick[4] = cards[e];
                            best = Math.Max(best, Score(pick));
                        }
                    }
                }
            }
        }

        return best;
    }

    public static PokerCategory CategoryOf(long score) => (PokerCategory)(score >> 20);

    /// <summary>El puntaje de exactamente 5 cartas.</summary>
    private static long Score(ReadOnlySpan<int> five)
    {
        Span<int> ranks = stackalloc int[5];
        for (var i = 0; i < 5; i++)
        {
            ranks[i] = RankOf(five[i]);
        }

        ranks.Sort();
        ranks.Reverse(); // de mayor a menor

        var flush = true;
        for (var i = 1; i < 5; i++)
        {
            flush &= SuitOf(five[i]) == SuitOf(five[0]);
        }

        // Escalera: cinco rangos seguidos; el as tambien vale 1 (A-2-3-4-5, la "rueda", cuyo mayor es el 5).
        var straightHigh = 0;
        if (ranks[0] - ranks[4] == 4 && DistinctCount(ranks) == 5)
        {
            straightHigh = ranks[0];
        }
        else if (ranks[0] == 14 && ranks[1] == 5 && ranks[2] == 4 && ranks[3] == 3 && ranks[4] == 2)
        {
            straightHigh = 5;
        }

        // Grupos por rango: primero los que mas se repiten y, a igual cantidad, los de mayor rango.
        Span<int> counts = stackalloc int[15];
        for (var i = 0; i < 5; i++)
        {
            counts[ranks[i]]++;
        }

        var groups = new List<(int Count, int Rank)>();
        for (var rank = 14; rank >= 2; rank--)
        {
            if (counts[rank] > 0)
            {
                groups.Add((counts[rank], rank));
            }
        }

        groups.Sort((x, y) => x.Count != y.Count ? y.Count.CompareTo(x.Count) : y.Rank.CompareTo(x.Rank));
        var ordered = groups.SelectMany(g => Enumerable.Repeat(g.Rank, g.Count)).ToArray();

        PokerCategory category;
        if (straightHigh > 0 && flush)
        {
            category = PokerCategory.StraightFlush;
            ordered = [straightHigh, 0, 0, 0, 0];
        }
        else if (groups[0].Count == 4)
        {
            category = PokerCategory.FourOfAKind;
        }
        else if (groups[0].Count == 3 && groups[1].Count == 2)
        {
            category = PokerCategory.FullHouse;
        }
        else if (flush)
        {
            category = PokerCategory.Flush;
        }
        else if (straightHigh > 0)
        {
            category = PokerCategory.Straight;
            ordered = [straightHigh, 0, 0, 0, 0];
        }
        else if (groups[0].Count == 3)
        {
            category = PokerCategory.ThreeOfAKind;
        }
        else if (groups[0].Count == 2 && groups[1].Count == 2)
        {
            category = PokerCategory.TwoPair;
        }
        else if (groups[0].Count == 2)
        {
            category = PokerCategory.Pair;
        }
        else
        {
            category = PokerCategory.HighCard;
        }

        long score = (long)category << 20;
        for (var i = 0; i < 5; i++)
        {
            score |= (long)ordered[i] << (16 - (4 * i));
        }

        return score;
    }

    private static int DistinctCount(ReadOnlySpan<int> ranks)
    {
        var distinct = 1;
        for (var i = 1; i < ranks.Length; i++)
        {
            if (ranks[i] != ranks[i - 1])
            {
                distinct++;
            }
        }

        return distinct;
    }
}
