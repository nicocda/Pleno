namespace Casino.Modules.Games.Poker;

/// <summary>
/// El bot de poker: reglas simples y deterministas (sin azar propio, asi una mano de bots es reproducible). Antes del flop juzga sus dos cartas (pares y cartas altas, mejor si son del
/// mismo palo o seguidas); despues juzga la mano que armo con el tablero. Apuesta fuerte con manos fuertes, iguala con medias si le sale barato y se retira con las flojas.
/// Siempre devuelve una jugada legal.
/// </summary>
internal static class PokerBot
{
    public static string Choose(PokerState s, int seat)
    {
        var p = s.Players[seat];
        var toCall = s.CurrentBet - p.Bet;
        var strength = s.Board.Count == 0 ? PreflopStrength(p.Cards) : PostflopStrength(p.Cards, s.Board);
        var (minTo, maxTo) = PokerGame.RaiseLimits(s, seat);
        var canRaise = p.Stack > toCall;
        var potOdds = toCall == 0 ? 0 : (double)toCall / (s.Pot + toCall);

        // Subida "de tamaño razonable": la mitad del pozo por encima de lo que hay que igualar.
        long RaiseTo(double factor) => Math.Clamp(s.CurrentBet + Math.Max(s.MinRaise, (long)(s.Pot * factor)), minTo, maxTo);

        if (strength >= 80 && canRaise)
        {
            return Raise(RaiseTo(0.75)); // mano muy fuerte
        }

        if (strength >= 60)
        {
            return canRaise && toCall <= s.BigBlind * 4 ? Raise(RaiseTo(0.5)) : Call(toCall);
        }

        if (strength >= 40)
        {
            return toCall == 0 ? """{"type":"check"}""" : potOdds <= 0.35 ? Call(toCall) : """{"type":"fold"}""";
        }

        // Mano floja: pasa si es gratis, y solo iguala la ciega grande o menos.
        if (toCall == 0)
        {
            return """{"type":"check"}""";
        }

        return toCall <= s.BigBlind && s.Board.Count == 0 && strength >= 30 ? Call(toCall) : """{"type":"fold"}""";
    }

    /// <summary>De 0 a 100: pares (los altos mas), cartas altas, mismo palo y cartas seguidas.</summary>
    private static int PreflopStrength(List<int> cards)
    {
        var a = PokerCards.RankOf(cards[0]);
        var b = PokerCards.RankOf(cards[1]);
        var high = Math.Max(a, b);
        var low = Math.Min(a, b);
        if (a == b)
        {
            return Math.Min(100, 55 + (high * 3)); // 22 = 61 ... AA = 97
        }

        var score = (high * 3) + low;
        if (PokerCards.SuitOf(cards[0]) == PokerCards.SuitOf(cards[1]))
        {
            score += 6;
        }

        if (high - low <= 2)
        {
            score += 4;
        }

        return Math.Min(95, score); // AKs = 57 + 6 + 4... acotado
    }

    /// <summary>De 0 a 100 segun la mejor mano que arma con el tablero (y, con una pareja, de que rango es).</summary>
    private static int PostflopStrength(List<int> hole, List<int> board)
    {
        var cards = new List<int>(hole);
        cards.AddRange(board);
        if (cards.Count < 5)
        {
            return 0;
        }

        var category = PokerCards.CategoryOf(PokerCards.Evaluate(cards));
        return category switch
        {
            PokerCategory.HighCard => 20,
            PokerCategory.Pair => PairStrength(hole, board),
            PokerCategory.TwoPair => 65,
            PokerCategory.ThreeOfAKind => 75,
            _ => 90,
        };
    }

    /// <summary>Una pareja vale mas si usa una carta propia (no solo la del tablero) y mas cuanto mas alta sea.</summary>
    private static int PairStrength(List<int> hole, List<int> board)
    {
        var boardRanks = board.Select(PokerCards.RankOf).ToHashSet();
        var holeRanks = hole.Select(PokerCards.RankOf).ToList();
        var paired = holeRanks.Where(r => boardRanks.Contains(r)).DefaultIfEmpty(0).Max();
        if (paired == 0)
        {
            return holeRanks[0] == holeRanks[1] ? 50 : 25; // pareja de bolsillo, o la pareja es del tablero (la tiene cualquiera)
        }

        return 38 + paired; // pareja con una carta propia: de 40 a 52
    }

    private static string Call(long toCall) => toCall == 0 ? """{"type":"check"}""" : """{"type":"call"}""";

    private static string Raise(long to) => $$"""{"type":"raise","to":{{to}}}""";
}
