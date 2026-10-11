using System.Text.Json;
using Casino.Modules.Games.Platform;
using Casino.Modules.Games.Tables;

namespace Casino.Modules.Games.Poker;

/// <summary>Lo que paso en la mano (para que el navegador lo cuente: "Jugador 3 subio a 40").</summary>
public sealed record PokerEvent(int Seat, string Kind, long? Amount = null, bool AllIn = false);

/// <summary>Lo que muestra un jugador en el showdown: sus cartas, que mano armo y si gano.</summary>
public sealed record PokerShowdown(int Seat, IReadOnlyList<int> Cards, string Category, bool Won);

/// <summary>Un jugador de la mano: sus cartas, su pila y lo que puso. Las cartas solo las ve el dueño (y todos si llegan al showdown).</summary>
public sealed class PokerPlayer
{
    public List<int> Cards { get; set; } = [];

    public long Stack { get; set; }

    /// <summary>Lo que puso en la ronda de apuestas actual.</summary>
    public long Bet { get; set; }

    /// <summary>Lo que puso en toda la mano (el pozo es la suma de todos).</summary>
    public long Total { get; set; }

    public bool Folded { get; set; }

    public bool AllIn { get; set; }

    /// <summary>Ya actuo desde la ultima subida (la ronda termina cuando todos actuaron y las apuestas se igualaron).</summary>
    public bool Acted { get; set; }
}

/// <summary>El estado completo de una mano de poker. Lo guarda la plataforma como JSON; solo <see cref="PokerGame.View"/> decide que se le muestra a cada jugador.</summary>
public sealed class PokerState
{
    public int Seats { get; set; }

    public long BuyIn { get; set; }

    public long SmallBlind { get; set; }

    public long BigBlind { get; set; }

    public int Dealer { get; set; }

    public int SmallBlindSeat { get; set; }

    public int BigBlindSeat { get; set; }

    /// <summary>La ronda: <c>preflop</c>, <c>flop</c>, <c>turn</c>, <c>river</c> o <c>done</c> (termino).</summary>
    public string Street { get; set; } = "preflop";

    public List<PokerPlayer> Players { get; set; } = [];

    public List<int> Board { get; set; } = [];

    /// <summary>Lo que queda del mazo, de arriba hacia abajo.</summary>
    public List<int> Deck { get; set; } = [];

    public int Current { get; set; }

    public long CurrentBet { get; set; }

    /// <summary>Cuanto tiene que subir como minimo (lo que subio la ultima subida completa, o la ciega grande).</summary>
    public long MinRaise { get; set; }

    public bool Done { get; set; }

    /// <summary>Lo que se lleva cada asiento del pozo al terminar.</summary>
    public List<long> Winnings { get; set; } = [];

    public List<PokerShowdown> Showdown { get; set; } = [];

    public string Seed { get; set; } = string.Empty;

    public string TableId { get; set; } = string.Empty;

    public List<PokerEvent> Events { get; set; } = [];

    public long Pot => Players.Sum(p => p.Total);
}

public sealed record PokerPlayerView(int Seat, long Stack, long Bet, bool Folded, bool AllIn, bool Acted, IReadOnlyList<int>? Cards, int CardCount);

/// <summary>Lo que ve un asiento: sus cartas, las comunitarias, el pozo, que debe pagar y que puede hacer (con los limites de una subida). Nunca las cartas de otros antes del showdown.</summary>
public sealed record PokerView(
    int? You,
    IReadOnlyList<int> Hand,
    IReadOnlyList<int> Board,
    string Street,
    long Pot,
    IReadOnlyList<PokerPlayerView> Players,
    int Dealer,
    int SmallBlindSeat,
    int BigBlindSeat,
    int Current,
    long CurrentBet,
    long ToCall,
    long MinRaiseTo,
    long MaxRaiseTo,
    IReadOnlyList<string> Actions,
    IReadOnlyList<PokerShowdown> Showdown,
    IReadOnlyList<long> Winnings,
    bool Done,
    IReadOnlyList<PokerEvent> Events);

/// <summary>
/// Texas Hold'em de 2 a 6 jugadores, UNA mano por mesa: cada uno entra con la misma cantidad de fichas, se juegan las cuatro rondas de apuestas (preflop, flop, turn y river) con
/// ciegas y limite libre (se puede subir hasta todo lo que se tenga) y se reparte el pozo. Como todos empiezan con lo mismo, hay un solo pozo (sin pozos laterales). Lo que se lleva
/// cada uno es lo que le queda de su entrada al terminar la mano.
/// </summary>
public sealed class PokerGame : ITableGame
{
    private const double TurnSeconds = 30;
    private const int MaxEvents = 24;

    public GameInfo Info { get; } = new("poker", "Poker", "Texas Hold'em de una mano: ciegas, flop, turn y river. De 2 a 6 jugadores, con amigos o bots.", "/poker", "P");

    public int MinPlayers => 2;

    public int MaxPlayers => 6;

    public long MinBuyIn => 20;

    public long MaxBuyIn => 2_000;

    public string Start(GameStartContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var n = context.Seats;
        var label = $"poker:{context.TableId:N}";
        var deck = PokerCards.NewDeck();
        FairShuffle.Shuffle(deck, context.Seed, label);

        var small = Math.Max(1, context.BuyIn / 50);
        var state = new PokerState
        {
            Seats = n,
            BuyIn = context.BuyIn,
            SmallBlind = small,
            BigBlind = small * 2,
            Seed = context.Seed,
            TableId = context.TableId.ToString("N"),
            Dealer = FairShuffle.Pick(context.Seed, $"{label}:dealer", n),
            Winnings = [.. Enumerable.Repeat(0L, n)],
        };

        // Cada asiento: una primera carta (posiciones 0..n-1) y una segunda (n..2n-1). Despues sigue el flop, el turn y el river.
        for (var seat = 0; seat < n; seat++)
        {
            state.Players.Add(new PokerPlayer { Stack = context.BuyIn, Cards = [deck[seat], deck[n + seat]] });
        }

        state.Deck = [.. deck.Skip(2 * n)];

        // Ciegas: con dos jugadores el que reparte (boton) pone la ciega chica y juega primero; con mas, la chica es la siguiente al boton.
        state.SmallBlindSeat = n == 2 ? state.Dealer : Next(state, state.Dealer);
        state.BigBlindSeat = Next(state, state.SmallBlindSeat);
        PostBlind(state, state.SmallBlindSeat, state.SmallBlind);
        PostBlind(state, state.BigBlindSeat, state.BigBlind);
        state.CurrentBet = state.BigBlind;
        state.MinRaise = state.BigBlind;
        state.Current = n == 2 ? state.Dealer : Next(state, state.BigBlindSeat);
        return Serialize(state);
    }

    public TurnInfo? Turn(string state)
    {
        var s = Deserialize(state);
        return s.Done ? null : new TurnInfo(s.Current, TurnSeconds);
    }

    public string Apply(string state, int seat, string action, DateTimeOffset now)
    {
        var s = Deserialize(state);
        if (s.Done)
        {
            throw new TableRuleException("finished", "La mano ya termino.");
        }

        if (seat != s.Current)
        {
            throw new TableRuleException("not_your_turn", "No es tu turno.");
        }

        var (type, to) = ParseAction(action);
        var p = s.Players[seat];
        var toCall = s.CurrentBet - p.Bet;
        switch (type)
        {
            case "fold":
                p.Folded = true;
                s.Events.Add(new PokerEvent(seat, "fold"));
                break;
            case "check":
                if (toCall > 0)
                {
                    throw new TableRuleException("cannot_check", $"Hay que pagar {toCall} para seguir: igualar, subir o retirarse.");
                }

                p.Acted = true;
                s.Events.Add(new PokerEvent(seat, "check"));
                break;
            case "call":
                if (toCall <= 0)
                {
                    throw new TableRuleException("nothing_to_call", "No hay nada que pagar: pasa o aposta.");
                }

                var paid = Put(p, toCall);
                p.Acted = true;
                s.Events.Add(new PokerEvent(seat, "call", paid, p.AllIn));
                break;
            case "raise":
                Raise(s, seat, to ?? throw new TableRuleException("bad_action", "Falta el monto al que subis."));
                break;
            default:
                throw new TableRuleException("bad_action", "La jugada no existe: usa fold, check, call o raise.");
        }

        AfterAction(s);
        if (s.Events.Count > MaxEvents)
        {
            s.Events.RemoveRange(0, s.Events.Count - MaxEvents);
        }

        return Serialize(s);
    }

    public string TimeoutAction(string state, int seat)
    {
        var s = Deserialize(state);
        return s.CurrentBet - s.Players[seat].Bet <= 0 ? """{"type":"check"}""" : """{"type":"fold"}""";
    }

    public string BotAction(string state, int seat) => PokerBot.Choose(Deserialize(state), seat);

    public object View(string state, int? seat)
    {
        var s = Deserialize(state);
        var revealed = s.Showdown.ToDictionary(x => x.Seat, x => x.Cards);
        var players = s.Players.Select((p, i) => new PokerPlayerView(
            i,
            p.Stack,
            p.Bet,
            p.Folded,
            p.AllIn,
            p.Acted,
            i == seat ? p.Cards : revealed.GetValueOrDefault(i),
            p.Cards.Count)).ToList();

        var myTurn = seat is { } who && who == s.Current && !s.Done;
        var (minTo, maxTo) = myTurn ? RaiseLimits(s, seat!.Value) : (0, 0);
        return new PokerView(
            seat,
            seat is { } me ? [.. s.Players[me].Cards] : [],
            s.Board,
            s.Street,
            s.Pot,
            players,
            s.Dealer,
            s.SmallBlindSeat,
            s.BigBlindSeat,
            s.Current,
            s.CurrentBet,
            seat is { } q ? Math.Max(0, s.CurrentBet - s.Players[q].Bet) : 0,
            minTo,
            maxTo,
            myTurn ? Legal(s, seat!.Value) : [],
            s.Showdown,
            s.Winnings,
            s.Done,
            s.Events);
    }

    public GameOutcome? Outcome(string state)
    {
        var s = Deserialize(state);
        if (!s.Done)
        {
            return null;
        }

        return new GameOutcome([.. s.Players.Select((p, i) => s.BuyIn - p.Total + s.Winnings[i])]);
    }

    // ---- Lo que se puede hacer ----

    /// <summary>Las jugadas legales del asiento que tiene el turno.</summary>
    internal static List<string> Legal(PokerState s, int seat)
    {
        var p = s.Players[seat];
        var toCall = s.CurrentBet - p.Bet;
        var legal = new List<string> { "fold" };
        legal.Add(toCall > 0 ? "call" : "check");
        if (p.Stack > toCall)
        {
            legal.Add("raise");
        }

        legal.Sort(StringComparer.Ordinal);
        return legal;
    }

    /// <summary>El minimo y el maximo "a cuanto" se puede subir: subir la ultima subida, o todo lo que se tiene (all-in), que siempre se puede aunque sea menos que el minimo.</summary>
    internal static (long MinTo, long MaxTo) RaiseLimits(PokerState s, int seat)
    {
        var p = s.Players[seat];
        var max = p.Bet + p.Stack;
        return (Math.Min(s.CurrentBet + s.MinRaise, max), max);
    }

    private static void Raise(PokerState s, int seat, long to)
    {
        var p = s.Players[seat];
        var toCall = s.CurrentBet - p.Bet;
        if (p.Stack <= toCall)
        {
            throw new TableRuleException("cannot_raise", "No te alcanzan las fichas para subir: podes igualar (all-in).");
        }

        var (minTo, maxTo) = RaiseLimits(s, seat);
        if (to < minTo || to > maxTo)
        {
            throw new TableRuleException("bad_raise", $"Podes subir a entre {minTo} y {maxTo} fichas.");
        }

        var amount = to - p.Bet;
        Put(p, amount);
        var previousBet = s.CurrentBet;
        var raiseSize = to - s.CurrentBet;
        if (raiseSize >= s.MinRaise)
        {
            s.MinRaise = raiseSize;
        }

        s.CurrentBet = to;
        foreach (var other in s.Players.Where(o => !o.Folded && !o.AllIn))
        {
            other.Acted = false; // una subida obliga a todos a responder otra vez
        }

        p.Acted = true;
        s.Events.Add(new PokerEvent(seat, previousBet == 0 ? "bet" : "raise", to, p.AllIn));
    }

    private static long Put(PokerPlayer p, long amount)
    {
        var paid = Math.Min(amount, p.Stack);
        p.Stack -= paid;
        p.Bet += paid;
        p.Total += paid;
        if (p.Stack == 0)
        {
            p.AllIn = true;
        }

        return paid;
    }

    private static void PostBlind(PokerState s, int seat, long amount)
    {
        var paid = Put(s.Players[seat], amount);
        s.Events.Add(new PokerEvent(seat, "blind", paid, s.Players[seat].AllIn));
    }

    // ---- Avance de la mano ----

    private static void AfterAction(PokerState s)
    {
        var live = Enumerable.Range(0, s.Seats).Where(i => !s.Players[i].Folded).ToList();
        if (live.Count == 1)
        {
            Finish(s, [live[0]], showdown: false);
            return;
        }

        while (true)
        {
            if (!RoundComplete(s))
            {
                s.Current = NextActor(s, s.Current);
                return;
            }

            if (s.Street == "river")
            {
                Showdown(s);
                return;
            }

            AdvanceStreet(s);
            if (Enumerable.Range(0, s.Seats).Count(i => !s.Players[i].Folded && !s.Players[i].AllIn) >= 2)
            {
                s.Current = NextActor(s, s.Dealer); // despues del flop juega primero el primero a la izquierda del boton
                return;
            }

            // Nadie mas puede apostar (todos all-in menos uno): se reparten las cartas que faltan hasta el showdown.
        }
    }

    private static bool RoundComplete(PokerState s)
    {
        var actors = s.Players.Where(p => !p.Folded && !p.AllIn).ToList();
        if (actors.Count == 0)
        {
            return true;
        }

        if (actors.Count == 1)
        {
            return actors[0].Bet >= s.Players.Max(p => p.Bet);
        }

        return actors.All(p => p.Acted && p.Bet == s.CurrentBet);
    }

    private static void AdvanceStreet(PokerState s)
    {
        foreach (var p in s.Players)
        {
            p.Bet = 0;
            p.Acted = false;
        }

        s.CurrentBet = 0;
        s.MinRaise = s.BigBlind;
        var (next, cards) = s.Street switch
        {
            "preflop" => ("flop", 3),
            "flop" => ("turn", 1),
            _ => ("river", 1),
        };
        for (var i = 0; i < cards; i++)
        {
            s.Board.Add(s.Deck[0]);
            s.Deck.RemoveAt(0);
        }

        s.Street = next;
        s.Events.Add(new PokerEvent(-1, next));
    }

    private static void Showdown(PokerState s)
    {
        var live = Enumerable.Range(0, s.Seats).Where(i => !s.Players[i].Folded).ToList();
        var scores = live.ToDictionary(i => i, i => PokerCards.Evaluate([.. s.Players[i].Cards, .. s.Board]));
        var best = scores.Values.Max();
        var winners = live.Where(i => scores[i] == best).ToList();
        s.Showdown = [.. live.Select(i => new PokerShowdown(i, [.. s.Players[i].Cards], PokerCards.CategoryOf(scores[i]).ToString(), winners.Contains(i)))];
        Finish(s, winners, showdown: true);
    }

    /// <summary>Reparte el pozo entre los ganadores (el resto de una division va al primero a la izquierda del boton).</summary>
    private static void Finish(PokerState s, List<int> winners, bool showdown)
    {
        var pot = s.Pot;
        var share = pot / winners.Count;
        var remainder = pot % winners.Count;
        foreach (var w in winners)
        {
            s.Winnings[w] += share;
        }

        var first = winners.OrderBy(w => (w - s.Dealer - 1 + s.Seats) % s.Seats).First();
        s.Winnings[first] += remainder;
        s.Done = true;
        s.Street = "done";
        foreach (var w in winners)
        {
            s.Events.Add(new PokerEvent(w, showdown ? "win_showdown" : "win", s.Winnings[w]));
        }
    }

    /// <summary>El proximo asiento con algo para decidir (que no se retiro ni esta all-in), empezando despues de <paramref name="from"/>.</summary>
    private static int NextActor(PokerState s, int from)
    {
        for (var i = 1; i <= s.Seats; i++)
        {
            var seat = (from + i) % s.Seats;
            if (!s.Players[seat].Folded && !s.Players[seat].AllIn)
            {
                return seat;
            }
        }

        return from;
    }

    private static int Next(PokerState s, int seat) => (seat + 1) % s.Seats;

    // ---- Formato ----

    private static (string Type, long? To) ParseAction(string action)
    {
        try
        {
            using var doc = JsonDocument.Parse(action);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString() ?? string.Empty;
            long? to = root.TryGetProperty("to", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : null;
            return (type, to);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new TableRuleException("bad_action", "La jugada no se entiende.");
        }
    }

    private static string Serialize(PokerState state) => JsonSerializer.Serialize(state, JsonOptions);

    internal static PokerState Deserialize(string state) => JsonSerializer.Deserialize<PokerState>(state, JsonOptions)!;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
