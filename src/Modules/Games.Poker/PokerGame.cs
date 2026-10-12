using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casino.Modules.Games.Fairness;
using Casino.Modules.Games.Platform;
using Casino.Modules.Games.Tables;

namespace Casino.Modules.Games.Poker;

/// <summary>Lo que paso en la mano (para que el navegador lo cuente: "Jugador 3 subio a 40").</summary>
public sealed record PokerEvent(int Seat, string Kind, long? Amount = null, bool AllIn = false);

/// <summary>Lo que muestra un jugador en el showdown: sus cartas, que mano armo y si gano.</summary>
public sealed record PokerShowdown(int Seat, IReadOnlyList<int> Cards, string Category, bool Won);

/// <summary>Un pozo de la mano (el principal o uno lateral): cuanto tiene, quienes pueden ganarlo (los que aportaron hasta ese nivel y siguen en la mano) y quienes lo ganaron.</summary>
public sealed record PokerPot(long Amount, List<int> Eligible, List<int> Winners);

/// <summary>Un jugador de la mesa: sus cartas, su pila y lo que puso en la mano. Las cartas solo las ve el dueño (y todos si llegan al showdown).</summary>
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

    /// <summary>Se quedo sin fichas: ya no juega manos (en la mano se lo trata como retirado y no recibe cartas).</summary>
    public bool Out { get; set; }

    /// <summary>Ya actuo desde la ultima subida (la ronda termina cuando todos actuaron y las apuestas se igualaron).</summary>
    public bool Acted { get; set; }
}

/// <summary>
/// El estado completo de una mesa de poker: la mano en curso y las pilas de todos (la partida sigue mano tras mano hasta que queda uno con fichas). Lo guarda la plataforma como JSON;
/// solo <see cref="PokerGame.View"/> decide que se le muestra a cada jugador.
/// </summary>
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

    /// <summary>La MANO termino (se pagaron los pozos); la partida sigue salvo que <see cref="Over"/>.</summary>
    public bool Done { get; set; }

    /// <summary>La PARTIDA termino: queda un solo jugador con fichas.</summary>
    public bool Over { get; set; }

    /// <summary>Numero de la mano en curso (desde 1).</summary>
    public int HandNo { get; set; }

    /// <summary>Lo que se lleva cada asiento de los pozos al terminar la mano (ya sumado a su pila).</summary>
    public List<long> Winnings { get; set; } = [];

    /// <summary>Los pozos de la mano ya repartidos (el principal y los laterales).</summary>
    public List<PokerPot> Pots { get; set; } = [];

    public List<PokerShowdown> Showdown { get; set; } = [];

    public string Seed { get; set; } = string.Empty;

    public string TableId { get; set; } = string.Empty;

    public List<PokerEvent> Events { get; set; } = [];

    public long Pot => Players.Sum(p => p.Total);
}

public sealed record PokerPlayerView(int Seat, long Stack, long Bet, bool Folded, bool AllIn, bool Acted, IReadOnlyList<int>? Cards, int CardCount, bool Out);

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
    IReadOnlyList<PokerEvent> Events,
    int HandNo,
    bool Over,
    string Commitment,
    string? HandSeed,
    IReadOnlyList<PokerPot> Pots);

/// <summary>
/// Texas Hold'em de 2 a 6 jugadores, mano tras mano: cada uno entra con la misma cantidad de fichas y se juegan manos completas (cuatro rondas de apuestas con ciegas y limite libre) hasta
/// que queda un solo jugador con fichas, que se lleva todo lo puesto. Como las pilas cambian de una mano a otra, un all-in por menos que los demas arma pozos laterales. Sin bots: quien se
/// ausenta pasa (si es gratis) o se retira. Cada mano tiene su propia semilla, derivada de la de la mesa, con su compromiso publicado antes de repartir y revelada al terminar la mano.
/// </summary>
public sealed class PokerGame : ITableGame
{
    private const double TurnSeconds = 30;
    private const double BetweenHandsSeconds = 6;
    private const int MaxEvents = 24;

    public GameInfo Info { get; } = new("poker", "Poker", "Texas Hold'em por manos: ciegas, flop, turn y river, hasta que queda uno con todas las fichas. De 2 a 6 jugadores.", "/poker", "P");

    public int MinPlayers => 2;

    public int MaxPlayers => 6;

    public long MinBuyIn => 20;

    public long MaxBuyIn => 2_000;

    public bool AllowsBots => false;

    public string Start(GameStartContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var n = context.Seats;
        var small = Math.Max(1, context.BuyIn / 50);
        var state = new PokerState
        {
            Seats = n,
            BuyIn = context.BuyIn,
            SmallBlind = small,
            BigBlind = small * 2,
            Seed = context.Seed,
            TableId = context.TableId.ToString("N"),
            Winnings = [.. Enumerable.Repeat(0L, n)],
        };
        for (var seat = 0; seat < n; seat++)
        {
            state.Players.Add(new PokerPlayer { Stack = context.BuyIn });
        }

        StartHand(state, 1);
        return Serialize(state);
    }

    /// <summary>La semilla de la mano <paramref name="handNo"/>: HMAC-SHA256 con la semilla de la mesa (hex en minuscula). Su compromiso se publica antes de repartir; se revela al terminar la mano.</summary>
    internal static string HandSeed(string tableSeed, string tableId, int handNo) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(tableSeed), Encoding.UTF8.GetBytes($"poker:{tableId}:hand:{handNo}")));

    /// <summary>
    /// Arma la mano <paramref name="handNo"/> con las pilas actuales: quienes no tienen fichas quedan afuera, el boton rota, se baraja con la semilla de la mano, se reparten las cartas
    /// (primera carta del k-esimo jugador en la posicion k, segunda en m + k, con m jugadores activos), se ponen las ciegas y se decide quien juega.
    /// </summary>
    private static void StartHand(PokerState s, int handNo)
    {
        s.HandNo = handNo;
        s.Done = false;
        s.Street = "preflop";
        s.Board = [];
        s.Showdown = [];
        s.Pots = [];
        s.Winnings = [.. Enumerable.Repeat(0L, s.Seats)];
        foreach (var p in s.Players)
        {
            p.Bet = 0;
            p.Total = 0;
            p.Acted = false;
            p.AllIn = false;
            p.Cards = [];
            p.Out = p.Stack <= 0;
            p.Folded = p.Out;
        }

        var active = Enumerable.Range(0, s.Seats).Where(i => !s.Players[i].Out).ToList();
        var m = active.Count;
        var handSeed = HandSeed(s.Seed, s.TableId, handNo);
        var label = $"poker:{s.TableId}:h{handNo}";
        var deck = PokerCards.NewDeck();
        FairShuffle.Shuffle(deck, handSeed, label);

        s.Dealer = handNo == 1 ? active[FairShuffle.Pick(handSeed, $"{label}:dealer", m)] : NextActive(s, s.Dealer);
        for (var k = 0; k < m; k++)
        {
            s.Players[active[k]].Cards = [deck[k], deck[m + k]];
        }

        s.Deck = [.. deck.Skip(2 * m)];

        // Ciegas: con dos jugadores el que reparte (boton) pone la ciega chica y juega primero; con mas, la chica es la siguiente al boton.
        s.SmallBlindSeat = m == 2 ? s.Dealer : NextActive(s, s.Dealer);
        s.BigBlindSeat = NextActive(s, s.SmallBlindSeat);
        s.Events.Add(new PokerEvent(-1, "hand", handNo));
        PostBlind(s, s.SmallBlindSeat, s.SmallBlind);
        PostBlind(s, s.BigBlindSeat, s.BigBlind);
        s.CurrentBet = s.BigBlind;
        s.MinRaise = s.BigBlind;
        s.Current = NextActor(s, m == 2 ? PreviousSeat(s, s.Dealer) : s.BigBlindSeat);

        // Si con las ciegas nadie puede decidir (pilas muy cortas), las cartas que faltan salen solas hasta el showdown.
        if (RoundComplete(s))
        {
            Progress(s, moveCurrent: false);
        }
    }

    public TurnInfo? Turn(string state)
    {
        var s = Deserialize(state);
        if (s.Over)
        {
            return null;
        }

        return s.Done ? new TurnInfo(s.Current, BetweenHandsSeconds, Automatic: true) : new TurnInfo(s.Current, TurnSeconds);
    }

    public string Apply(string state, int seat, string action, DateTimeOffset now)
    {
        var s = Deserialize(state);
        if (s.Over)
        {
            throw new TableRuleException("finished", "La partida ya termino.");
        }

        if (seat != s.Current)
        {
            throw new TableRuleException("not_your_turn", "No es tu turno.");
        }

        var (type, to) = ParseAction(action);
        if (s.Done)
        {
            // Entre manos solo se acepta pasar a la siguiente (la pausa vencio, o el dueño del turno no quiso esperar).
            if (type != "next")
            {
                throw new TableRuleException("hand_over", "La mano ya termino: espera la siguiente.");
            }

            StartHand(s, s.HandNo + 1);
            TrimEvents(s);
            return Serialize(s);
        }

        if (type == "next")
        {
            throw new TableRuleException("hand_running", "La mano todavia no termino.");
        }

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
        TrimEvents(s);
        return Serialize(s);
    }

    /// <summary>Lo que se hace por quien no actuo a tiempo: pasar a la mano siguiente si la anterior termino, pasar si es gratis, retirarse si no.</summary>
    public string TimeoutAction(string state, int seat)
    {
        var s = Deserialize(state);
        if (s.Done)
        {
            return """{"type":"next"}""";
        }

        return s.CurrentBet - s.Players[seat].Bet <= 0 ? """{"type":"check"}""" : """{"type":"fold"}""";
    }

    /// <summary>Quien esta ausente no apuesta (no hay bots de verdad): hace lo mismo que si se le vence el turno.</summary>
    public string BotAction(string state, int seat) => TimeoutAction(state, seat);

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
            p.Cards.Count,
            p.Out)).ToList();

        var handSeed = HandSeed(s.Seed, s.TableId, s.HandNo);
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
            s.Events,
            s.HandNo,
            s.Over,
            ProvablyFair.Commitment(handSeed),
            s.Done ? handSeed : null,
            s.Pots);
    }

    public GameOutcome? Outcome(string state)
    {
        var s = Deserialize(state);
        if (!s.Over)
        {
            return null;
        }

        // Quien se queda con todas las fichas cobra todo lo que se puso (la suma de las pilas es siempre entrada x asientos).
        return new GameOutcome([.. s.Players.Select(p => p.Stack)]);
    }

    // ---- Lo que se puede hacer ----

    /// <summary>Las jugadas legales del asiento que tiene el turno.</summary>
    internal static List<string> Legal(PokerState s, int seat)
    {
        var p = s.Players[seat];
        var toCall = s.CurrentBet - p.Bet;
        var legal = new List<string> { "fold" };
        legal.Add(toCall > 0 ? "call" : "check");
        if (CanRaise(s, seat))
        {
            legal.Add("raise");
        }

        legal.Sort(StringComparer.Ordinal);
        return legal;
    }

    /// <summary>Se puede subir si sobran fichas despues de igualar y queda alguien que pueda responder (si todos los demas estan all-in, subir no tiene sentido).</summary>
    internal static bool CanRaise(PokerState s, int seat) =>
        s.Players[seat].Stack > s.CurrentBet - s.Players[seat].Bet
        && s.Players.Where((o, i) => i != seat && !o.Folded && !o.AllIn).Any();

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
        if (!CanRaise(s, seat))
        {
            throw new TableRuleException("cannot_raise", "No podes subir: no te alcanzan las fichas, o nadie mas puede responder. Podes igualar.");
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
            Finish(s, showdown: false);
            return;
        }

        Progress(s, moveCurrent: true);
    }

    /// <summary>
    /// Avanza la mano despues de una accion (o al repartir): si la ronda sigue, pasa el turno (<paramref name="moveCurrent"/>); si se cerro, da la carta que sigue y, cuando nadie mas puede
    /// apostar, reparte lo que falta hasta el showdown.
    /// </summary>
    private static void Progress(PokerState s, bool moveCurrent)
    {
        while (true)
        {
            if (!RoundComplete(s))
            {
                if (moveCurrent)
                {
                    s.Current = NextActor(s, s.Current);
                }

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
            moveCurrent = false;
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
        Finish(s, showdown: true, scores);
    }

    /// <summary>
    /// Termina la mano: arma los pozos (principal y laterales), reparte cada uno entre los mejores de quienes pueden ganarlo y suma lo ganado a las pilas. Si queda un solo jugador con fichas,
    /// la partida termina; si no, quienes se quedaron sin fichas quedan afuera y empieza la pausa hasta la mano siguiente.
    /// </summary>
    private static void Finish(PokerState s, bool showdown, Dictionary<int, long>? scores = null)
    {
        var live = Enumerable.Range(0, s.Seats).Where(i => !s.Players[i].Folded).ToList();
        s.Pots = BuildPots(s);
        for (var k = 0; k < s.Pots.Count; k++)
        {
            var pot = s.Pots[k];
            var eligible = pot.Eligible;
            var contest = showdown && eligible.Count > 1;
            var best = contest ? eligible.Max(i => scores![i]) : 0;
            var winners = contest ? eligible.Where(i => scores![i] == best).ToList() : [.. eligible];
            var share = pot.Amount / winners.Count;
            foreach (var w in winners)
            {
                s.Winnings[w] += share;
            }

            // El resto de una division va al primero de los ganadores a la izquierda del boton.
            s.Winnings[winners.OrderBy(w => (w - s.Dealer - 1 + s.Seats) % s.Seats).First()] += pot.Amount % winners.Count;
            s.Pots[k] = pot with { Winners = winners };
        }

        if (showdown)
        {
            var won = s.Pots.SelectMany(p => p.Winners).ToHashSet();
            s.Showdown = [.. live.Select(i => new PokerShowdown(i, [.. s.Players[i].Cards], PokerCards.CategoryOf(scores![i]).ToString(), won.Contains(i)))];
        }

        for (var i = 0; i < s.Seats; i++)
        {
            s.Players[i].Stack += s.Winnings[i];
        }

        s.Done = true;
        s.Street = "done";
        s.Current = s.Dealer; // el turno "automatico" de la pausa entre manos
        foreach (var w in Enumerable.Range(0, s.Seats).Where(i => s.Winnings[i] > 0))
        {
            s.Events.Add(new PokerEvent(w, showdown ? "win_showdown" : "win", s.Winnings[w]));
        }

        foreach (var i in Enumerable.Range(0, s.Seats).Where(i => !s.Players[i].Out && s.Players[i].Stack == 0))
        {
            s.Events.Add(new PokerEvent(i, "out"));
        }

        s.Over = s.Players.Count(p => p.Stack > 0) <= 1;
    }

    /// <summary>
    /// Los pozos de la mano: uno por cada nivel de aporte de quienes siguen en la mano. Cada pozo suma lo que cada jugador (tambien los retirados) puso entre un nivel y el anterior y lo pueden ganar
    /// los que siguen y llegaron a ese nivel. Lo que un retirado puso por encima del mayor nivel va al ultimo pozo. Una apuesta que nadie igualo queda como un pozo de un solo jugador: vuelve a el.
    /// </summary>
    internal static List<PokerPot> BuildPots(PokerState s)
    {
        var levels = s.Players.Where(p => !p.Folded && p.Total > 0).Select(p => p.Total).Distinct().Order().ToList();
        var pots = new List<PokerPot>();
        long previous = 0;
        foreach (var level in levels)
        {
            var amount = s.Players.Sum(p => Math.Min(p.Total, level) - Math.Min(p.Total, previous));
            var eligible = Enumerable.Range(0, s.Seats).Where(i => !s.Players[i].Folded && s.Players[i].Total >= level).ToList();
            pots.Add(new PokerPot(amount, eligible, []));
            previous = level;
        }

        var extra = s.Players.Sum(p => Math.Max(0, p.Total - previous));
        if (extra > 0 && pots.Count > 0)
        {
            pots[^1] = pots[^1] with { Amount = pots[^1].Amount + extra };
        }

        return pots;
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

    /// <summary>El proximo asiento que sigue jugando manos (con fichas), empezando despues de <paramref name="seat"/>.</summary>
    private static int NextActive(PokerState s, int seat)
    {
        for (var i = 1; i <= s.Seats; i++)
        {
            var candidate = (seat + i) % s.Seats;
            if (!s.Players[candidate].Out)
            {
                return candidate;
            }
        }

        return seat;
    }

    private static int PreviousSeat(PokerState s, int seat) => (seat + s.Seats - 1) % s.Seats;

    private static void TrimEvents(PokerState s)
    {
        if (s.Events.Count > MaxEvents)
        {
            s.Events.RemoveRange(0, s.Events.Count - MaxEvents);
        }
    }

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
