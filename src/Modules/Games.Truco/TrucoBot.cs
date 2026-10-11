namespace Casino.Modules.Games.Truco;

/// <summary>
/// El bot de Truco: reglas simples y deterministas (sin azar propio, asi una partida de bots es reproducible). Acepta el envido con buenos puntos y el truco con cartas
/// fuertes, sube cuando tiene mucho, tira la carta mas baja que le gana al rival (o la mas baja si no le puede ganar) y se va al mazo solo si no le queda otra.
/// Siempre devuelve una jugada legal.
/// </summary>
internal static class TrucoBot
{
    public static string Choose(TrucoState s, int seat)
    {
        var legal = TrucoGame.Legal(s, seat);
        var choice = Decide(s, seat, legal);
        if (choice is not null && legal.Contains(choice.Value.Type))
        {
            return Json(choice.Value.Type, choice.Value.Card);
        }

        // Respaldo: lo mas seguro que haya.
        if (legal.Contains("play"))
        {
            return Json("play", s.Hands[seat].OrderBy(TrucoCards.StrengthOf).First());
        }

        return Json(legal.Contains("no_quiero") ? "no_quiero" : legal.First());
    }

    private static (string Type, int? Card)? Decide(TrucoState s, int seat, HashSet<string> legal)
    {
        var hand = s.Hands[seat];
        var power = Power(hand);

        if (s.Pending is { } pending)
        {
            if (pending.Kind == "envido")
            {
                var points = TrucoCards.EnvidoPoints(s.Dealt[seat]);
                if (points >= 31 && legal.Contains("real_envido"))
                {
                    return ("real_envido", null);
                }

                var threshold = pending.Calls.Count == 1 && pending.Calls[0] == "envido" ? 24 : 27;
                return (points >= threshold ? "quiero" : "no_quiero", null);
            }

            if (power >= 30 && legal.Contains("retruco"))
            {
                return ("retruco", null);
            }

            if (power >= 33 && legal.Contains("vale4"))
            {
                return ("vale4", null);
            }

            return (power >= 20 ? "quiero" : "no_quiero", null);
        }

        // Canta truco (o sube) con una mano fuerte.
        foreach (var raise in new[] { "truco", "retruco", "vale4" })
        {
            if (legal.Contains(raise) && power >= (raise == "vale4" ? 34 : 28))
            {
                return (raise, null);
            }
        }

        // Canta envido con buenos puntos, en la primera baza.
        if (legal.Contains("envido") && TrucoCards.EnvidoPoints(s.Dealt[seat]) >= 28)
        {
            return ("envido", null);
        }

        if (hand.Count == 0)
        {
            return null;
        }

        var ordered = hand.OrderBy(TrucoCards.StrengthOf).ToList();
        var opponentPlayed = s.Table.Count % 2 == 1 && s.Table[^1].Seat != seat;
        if (opponentPlayed)
        {
            // Responde con la carta mas baja que le gana; si ninguna le gana, tira la mas baja.
            var toBeat = TrucoCards.StrengthOf(s.Table[^1].Card);
            var winner = ordered.Where(c => TrucoCards.StrengthOf(c) > toBeat).Select(c => (int?)c).FirstOrDefault();
            return ("play", winner ?? ordered[0]);
        }

        // Sale: en la primera baza con la del medio; despues con la mas fuerte que le quede.
        var lead = s.Bazas.Count == 0 && ordered.Count == 3 ? ordered[1] : ordered[^1];
        return ("play", lead);
    }

    /// <summary>Que tan buena es la mano: el promedio de fuerza de las cartas que le quedan, llevado a 3 cartas (mas de 20 es una mano pareja, mas de 28 es muy buena).</summary>
    private static int Power(List<int> hand) => hand.Count == 0 ? 0 : (int)Math.Round(hand.Average(TrucoCards.StrengthOf) * 3);

    private static string Json(string type, int? card = null) => card is { } c ? $$"""{"type":"{{type}}","card":{{c}}}""" : $$"""{"type":"{{type}}"}""";
}
