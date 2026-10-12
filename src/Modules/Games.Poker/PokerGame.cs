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

    /// <summary>Cuantas recompras uso.</summary>
    public int RebuysUsed { get; set; }

    /// <summary>La pila con la que arranco la mano (desempata quien queda mejor ubicado cuando se eliminan dos en la misma mano).</summary>
    public long StartStack { get; set; }

    /// <summary>Se quedo sin fichas, le quedan recompras y tiene que decidir si recompra o no (hasta que vence la pausa).</summary>
    public bool AwaitingRebuy { get; set; }

    /// <summary>Ya tiene su lugar en el ranking de eliminados.</summary>
    public bool Ranked { get; set; }
}

/// <summary>
/// El estado completo de una mesa de poker: la mano en curso y las pilas de todos (la partida sigue mano tras mano hasta que queda uno con fichas). Lo guarda la plataforma como JSON;
/// solo <see cref="PokerGame.View"/> decide que se le muestra a cada jugador.
/// </summary>
public sealed class PokerState
{
    public int Seats { get; set; }

    public long BuyIn { get; set; }

    /// <summary>La ciega chica de la mano en curso (la mitad de la grande).</summary>
    public long SmallBlind { get; set; }

    /// <summary>La ciega grande de la mano en curso: sube cada <see cref="LevelHands"/> manos.</summary>
    public long BigBlind { get; set; }

    /// <summary>La ciega grande con la que arranco la mesa.</summary>
    public long BigBlind0 { get; set; }

    /// <summary>Cada cuantas manos sube el nivel y cuanto (porcentaje): se guardan al crear la mesa.</summary>
    public int LevelHands { get; set; } = 10;

    public int GrowthPercent { get; set; } = 50;

    /// <summary>La comision de la casa por cada entrada (la inicial y cada recompra). No entra al premio.</summary>
    public long Fee { get; set; }

    /// <summary>Cuantas recompras permite la mesa por jugador.</summary>
    public int Rebuys { get; set; }

    /// <summary>Cuantas recompras se hicieron en total (cada una suma una entrada al premio).</summary>
    public int RebuyCount { get; set; }

    /// <summary>Cuanto dura la pausa hasta la mano siguiente (mas larga si alguien puede recomprar).</summary>
    public double PauseSeconds { get; set; }

    /// <summary>Los asientos eliminados, del primero al ultimo en irse: ordena el ranking.</summary>
    public List<int> Eliminated { get; set; } = [];

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

    /// <summary>Lo que se reparte entre los primeros: una entrada por cada asiento y por cada recompra (sin la comision).</summary>
    public long PrizePool => BuyIn * (Seats + RebuyCount);

    /// <summary>Cuantas manos faltan para que suba el nivel de ciegas (contando la mano en curso).</summary>
    public int HandsToNextLevel() => LevelHands - ((HandNo - 1) % LevelHands);
}

public sealed record PokerPlayerView(int Seat, long Stack, long Bet, bool Folded, bool AllIn, bool Acted, IReadOnlyList<int>? Cards, int CardCount, bool Out, int RebuysLeft, bool AwaitingRebuy);

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
    IReadOnlyList<PokerPot> Pots,
    long SmallBlind,
    long BigBlind,
    long NextBigBlind,
    int HandsToNextLevel,
    int Rebuys,
    long Entry,
    long Fee,
    long PrizePool,
    IReadOnlyList<long> Prizes,
    IReadOnlyList<int> Ranking);

/// <summary>
/// Texas Hold'em de 2 a 6 jugadores, mano tras mano: cada uno entra con la misma cantidad de fichas y se juegan manos completas (cuatro rondas de apuestas con ciegas y limite libre) hasta
/// que queda un solo jugador con fichas, que se lleva todo lo puesto. Como las pilas cambian de una mano a otra, un all-in por menos que los demas arma pozos laterales. Sin bots: quien se
/// ausenta pasa (si es gratis) o se retira. Cada mano tiene su propia semilla, derivada de la de la mesa, con su compromiso publicado antes de repartir y revelada al terminar la mano.
/// </summary>
public sealed class PokerGame : ITableGame, IConfigurableGame
{
    private const double TurnSeconds = 30;
    private const int MaxEvents = 24;

    private static readonly JsonSerializerOptions OptionsJson = new(JsonSerializerDefaults.Web);

    private PokerRules _rules = new();

    /// <summary>Lo que la casa fija para todas las mesas (comision, niveles, pausas).</summary>
    public void Configure(Microsoft.Extensions.Configuration.IConfiguration configuration) => _rules = PokerRules.Load(configuration);

    public GameInfo Info { get; } = new("poker", "Poker", "Texas Hold'em por manos: ciegas, flop, turn y river, hasta que queda uno con todas las fichas. De 2 a 6 jugadores.", "/poker", "P");

    public int MinPlayers => 2;

    public int MaxPlayers => 6;

    public long MinBuyIn => 40;

    public long MaxBuyIn => 2_000;

    public bool AllowsBots => false;

    public IReadOnlyList<TableOption> Options { get; } =
    [
        new("bigBlind", "Ciega grande", "La ciega chica es la mitad. La entrada tiene que valer entre 20 y 100 ciegas grandes y las ciegas suben solas cada 10 manos.", "number", Min: 2),
        new("rebuys", "Recompras", "Cuántas veces puede volver a entrar quien se queda sin fichas (cuesta una entrada más su comisión). Lo que reservás de más se devuelve si no lo usás.", "choice", Choices: [0, 1, 2], Default: 0),
    ];

    /// <summary>Lo que se elige al crear la mesa, ya validado: las ciegas, las recompras y lo que fija la casa (comision, niveles), que queda guardado en la mesa.</summary>
    private sealed record PokerTableOptions(long BigBlind, int Rebuys, long Fee, int LevelHands, int GrowthPercent);

    private static long DefaultBigBlind(long buyIn) => 2 * Math.Max(1, buyIn / 50);

    public TableQuote Quote(long buyIn, int maxPlayers, JsonElement? options)
    {
        var bigBlind = DefaultBigBlind(buyIn);
        var rebuys = 0;
        if (options is { ValueKind: JsonValueKind.Object } o)
        {
            if (o.TryGetProperty("bigBlind", out var bb) && bb.ValueKind != JsonValueKind.Null)
            {
                bigBlind = bb.ValueKind == JsonValueKind.Number && bb.TryGetInt64(out var v) ? v : throw new TableRuleException("bad_option", "La ciega grande tiene que ser un numero entero.");
            }

            if (o.TryGetProperty("rebuys", out var rb) && rb.ValueKind != JsonValueKind.Null)
            {
                rebuys = rb.ValueKind == JsonValueKind.Number && rb.TryGetInt32(out var v) ? v : throw new TableRuleException("bad_option", "Las recompras tienen que ser un numero entero.");
            }
        }

        if (bigBlind < 2 || bigBlind % 2 != 0)
        {
            throw new TableRuleException("bad_blind", "La ciega grande tiene que ser par y de 2 o mas (la chica es la mitad).");
        }

        if (buyIn < bigBlind * _rules.MinEntryInBigBlinds || buyIn > bigBlind * _rules.MaxEntryInBigBlinds)
        {
            throw new TableRuleException("bad_blind", $"Con una entrada de {buyIn} la ciega grande tiene que estar entre {Math.Max(2, (buyIn + _rules.MaxEntryInBigBlinds - 1) / _rules.MaxEntryInBigBlinds)} y {buyIn / _rules.MinEntryInBigBlinds} (la entrada vale entre {_rules.MinEntryInBigBlinds} y {_rules.MaxEntryInBigBlinds} ciegas grandes).");
        }

        if (rebuys < 0 || rebuys > _rules.MaxRebuys)
        {
            throw new TableRuleException("bad_rebuys", $"Las recompras van de 0 a {_rules.MaxRebuys}.");
        }

        var fee = buyIn * _rules.EntryFeePermille / 1000;
        var entries = 1 + rebuys;
        var stake = (buyIn + fee) * entries;
        var chosen = new PokerTableOptions(bigBlind, rebuys, fee, _rules.BlindLevelHands, _rules.BlindGrowthPercent);
        var description = $"Ciegas {bigBlind / 2}/{bigBlind} (suben cada {_rules.BlindLevelHands} manos) · {(rebuys == 0 ? "sin recompras" : rebuys == 1 ? "1 recompra" : $"{rebuys} recompras")}";
        var breakdown = rebuys == 0
            ? $"Se reservan {stake} fichas: la entrada de {buyIn} más {fee} de comisión."
            : $"Se reservan {stake} fichas: {entries} entradas posibles de {buyIn} más {fee} de comisión cada una. Las que no uses se te devuelven al terminar.";
        return new TableQuote(stake, JsonSerializer.Serialize(chosen, OptionsJson), description, breakdown);
    }

    /// <summary>Puede decidir la recompra aunque no sea su turno: cualquiera que se quedo sin fichas y todavia no decidio, durante la pausa.</summary>
    public bool MayAct(string state, int seat)
    {
        var s = Deserialize(state);
        if (s.Over)
        {
            return false;
        }

        return s.Done ? seat == s.Current || s.Players[seat].AwaitingRebuy : seat == s.Current;
    }

    public string Start(GameStartContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var n = context.Seats;
        var chosen = context.Options is null
            ? new PokerTableOptions(DefaultBigBlind(context.BuyIn), 0, 0, _rules.BlindLevelHands, _rules.BlindGrowthPercent)
            : JsonSerializer.Deserialize<PokerTableOptions>(context.Options, OptionsJson)!;
        var state = new PokerState
        {
            Seats = n,
            BuyIn = context.BuyIn,
            BigBlind0 = chosen.BigBlind,
            Fee = chosen.Fee,
            Rebuys = chosen.Rebuys,
            LevelHands = chosen.LevelHands,
            GrowthPercent = chosen.GrowthPercent,
            PauseSeconds = _rules.BetweenHandsSeconds,
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

    /// <summary>La ciega grande de la mano <paramref name="handNo"/>: sube <paramref name="growthPercent"/> % cada <paramref name="levelHands"/> manos (redondeada hacia arriba y a par).</summary>
    internal static long BigBlindAt(long bigBlind0, int handNo, int levelHands, int growthPercent)
    {
        var level = (handNo - 1) / Math.Max(1, levelHands);
        var bb = bigBlind0;
        for (var i = 0; i < level; i++)
        {
            var next = ((bb * (100 + growthPercent)) + 99) / 100;
            if (next % 2 != 0)
            {
                next++;
            }

            bb = Math.Max(next, bb + 2);
        }

        return bb;
    }

    /// <summary>La semilla de la mano <paramref name="handNo"/>: HMAC-SHA256 con la semilla de la mesa (hex en minuscula). Su compromiso se publica antes de repartir; se revela al terminar la mano.</summary>
    internal static string HandSeed(string tableSeed, string tableId, int handNo) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(tableSeed), Encoding.UTF8.GetBytes($"poker:{tableId}:hand:{handNo}")));

    /// <summary>
    /// Arma la mano <paramref name="handNo"/> con las pilas actuales: quienes no tienen fichas quedan afuera, el boton rota, se baraja con la semilla de la mano, se reparten las cartas
    /// (primera carta del k-esimo jugador en la posicion k, segunda en m + k, con m jugadores activos), se ponen las ciegas y se decide quien juega.
    /// </summary>
    private void StartHand(PokerState s, int handNo)
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
            p.AwaitingRebuy = false;
            p.Out = p.Out || p.Stack <= 0;
            p.Folded = p.Out;
            p.StartStack = p.Stack;
        }

        s.BigBlind = BigBlindAt(s.BigBlind0, handNo, s.LevelHands, s.GrowthPercent);
        s.SmallBlind = s.BigBlind / 2;

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

        return s.Done ? new TurnInfo(s.Current, s.PauseSeconds > 0 ? s.PauseSeconds : _rules.BetweenHandsSeconds, Automatic: true) : new TurnInfo(s.Current, TurnSeconds);
    }

    public string Apply(string state, int seat, string action, DateTimeOffset now)
    {
        var s = Deserialize(state);
        if (s.Over)
        {
            throw new TableRuleException("finished", "La partida ya termino.");
        }

        var (type, to) = ParseAction(action);
        if (s.Done)
        {
            // Entre manos: quien se quedo sin fichas decide si recompra; la pausa vencida (o quien tiene el turno) pasa a la mano siguiente.
            switch (type)
            {
                case "rebuy":
                    Rebuy(s, seat);
                    break;
                case "decline":
                    Decline(s, seat);
                    break;
                case "next":
                    if (seat != s.Current)
                    {
                        throw new TableRuleException("not_your_turn", "No es tu turno.");
                    }

                    ResolvePending(s);
                    if (!s.Over)
                    {
                        StartHand(s, s.HandNo + 1);
                    }

                    break;
                default:
                    throw new TableRuleException("hand_over", "La mano ya termino: espera la siguiente.");
            }

            TrimEvents(s);
            return Serialize(s);
        }

        if (seat != s.Current)
        {
            throw new TableRuleException("not_your_turn", "No es tu turno.");
        }

        if (type is "rebuy" or "decline")
        {
            throw new TableRuleException("hand_running", "La mano todavia no termino.");
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
            p.Out,
            s.Rebuys - p.RebuysUsed,
            p.AwaitingRebuy)).ToList();

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
            myTurn ? Legal(s, seat!.Value) : s is { Done: true, Over: false } && seat is { } waiting && s.Players[waiting].AwaitingRebuy ? ["decline", "rebuy"] : [],
            s.Showdown,
            s.Winnings,
            s.Done,
            s.Events,
            s.HandNo,
            s.Over,
            ProvablyFair.Commitment(handSeed),
            s.Done ? handSeed : null,
            s.Pots,
            s.SmallBlind,
            s.BigBlind,
            BigBlindAt(s.BigBlind0, s.HandNo + s.HandsToNextLevel(), s.LevelHands, s.GrowthPercent),
            s.HandsToNextLevel(),
            s.Rebuys,
            s.BuyIn,
            s.Fee,
            s.PrizePool,
            Prizes(s.PrizePool, s.Seats),
            s.Over ? Ranking(s) : []);
    }

    public GameOutcome? Outcome(string state)
    {
        var s = Deserialize(state);
        if (!s.Over)
        {
            return null;
        }

        // El premio se reparte segun el lugar en el ranking (el que queda con todas las fichas es el primero) y cada uno recupera las recompras que reservo y no uso.
        var prizes = Prizes(s.PrizePool, s.Seats);
        var ranking = Ranking(s);
        var payouts = new long[s.Seats];
        for (var place = 0; place < ranking.Count; place++)
        {
            payouts[ranking[place]] = place < prizes.Count ? prizes[place] : 0;
        }

        for (var seat = 0; seat < s.Seats; seat++)
        {
            payouts[seat] += (s.Rebuys - s.Players[seat].RebuysUsed) * (s.BuyIn + s.Fee);
        }

        return new GameOutcome(payouts, HouseTake: (s.Seats + s.RebuyCount) * s.Fee);
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

    private void AfterAction(PokerState s)
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
    private void Progress(PokerState s, bool moveCurrent)
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

    private void Showdown(PokerState s)
    {
        var live = Enumerable.Range(0, s.Seats).Where(i => !s.Players[i].Folded).ToList();
        var scores = live.ToDictionary(i => i, i => PokerCards.Evaluate([.. s.Players[i].Cards, .. s.Board]));
        Finish(s, showdown: true, scores);
    }

    /// <summary>
    /// Termina la mano: arma los pozos (principal y laterales), reparte cada uno entre los mejores de quienes pueden ganarlo y suma lo ganado a las pilas. Si queda un solo jugador con fichas,
    /// la partida termina; si no, quienes se quedaron sin fichas quedan afuera y empieza la pausa hasta la mano siguiente.
    /// </summary>
    private void Finish(PokerState s, bool showdown, Dictionary<int, long>? scores = null)
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

        // Quien se queda sin fichas: si le quedan recompras decide en la pausa; si no, queda afuera.
        foreach (var i in Enumerable.Range(0, s.Seats).Where(i => !s.Players[i].Out && s.Players[i].Stack == 0))
        {
            if (s.Players[i].RebuysUsed < s.Rebuys)
            {
                s.Players[i].AwaitingRebuy = true;
            }
            else
            {
                Eliminate(s, i);
            }
        }

        var waiting = s.Players.Any(p => p.AwaitingRebuy);
        s.PauseSeconds = waiting ? _rules.RebuyWindowSeconds : _rules.BetweenHandsSeconds;
        if (!waiting)
        {
            SettleRanks(s);
            s.Over = s.Players.Count(p => p.Stack > 0) <= 1;
        }
    }

    /// <summary>Queda afuera (sin recompras): se anota el evento; su lugar en el ranking se fija cuando se resuelven todos los de la mano (<see cref="SettleRanks"/>).</summary>
    private static void Eliminate(PokerState s, int seat)
    {
        s.Players[seat].Out = true;
        s.Players[seat].AwaitingRebuy = false;
        s.Events.Add(new PokerEvent(seat, "out"));
    }

    /// <summary>Fija el lugar de los eliminados de esta mano: se van primero los que arrancaron con menos fichas (a igualdad, el de numero de asiento menor).</summary>
    private static void SettleRanks(PokerState s)
    {
        foreach (var seat in Enumerable.Range(0, s.Seats).Where(i => s.Players[i].Out && !s.Players[i].Ranked).OrderBy(i => s.Players[i].StartStack).ThenBy(i => i))
        {
            s.Eliminated.Add(seat);
            s.Players[seat].Ranked = true;
        }
    }

    /// <summary>Decidieron todos los que podian recomprar: si queda uno solo con fichas termina la partida; si no, la pausa se acorta.</summary>
    private void AfterDecision(PokerState s)
    {
        if (s.Players.Any(p => p.AwaitingRebuy))
        {
            return;
        }

        SettleRanks(s);
        if (s.Players.Count(p => p.Stack > 0) <= 1)
        {
            s.Over = true;
        }
        else
        {
            s.PauseSeconds = _rules.AfterDecisionsSeconds;
        }
    }

    private void Rebuy(PokerState s, int seat)
    {
        var p = s.Players[seat];
        if (!p.AwaitingRebuy || p.RebuysUsed >= s.Rebuys)
        {
            throw new TableRuleException("cannot_rebuy", "No podes recomprar ahora.");
        }

        p.Stack = s.BuyIn;
        p.StartStack = s.BuyIn;
        p.RebuysUsed++;
        p.AwaitingRebuy = false;
        s.RebuyCount++;
        s.Events.Add(new PokerEvent(seat, "rebuy", s.BuyIn));
        AfterDecision(s);
    }

    private void Decline(PokerState s, int seat)
    {
        if (!s.Players[seat].AwaitingRebuy)
        {
            throw new TableRuleException("cannot_decline", "No tenes una recompra pendiente.");
        }

        Eliminate(s, seat);
        AfterDecision(s);
    }

    /// <summary>La pausa vencio: los que no recompraron quedan afuera y, si queda uno solo con fichas, la partida termina.</summary>
    private static void ResolvePending(PokerState s)
    {
        foreach (var seat in Enumerable.Range(0, s.Seats).Where(i => s.Players[i].AwaitingRebuy))
        {
            Eliminate(s, seat);
        }

        SettleRanks(s);
        s.Over = s.Players.Count(p => p.Stack > 0) <= 1;
    }

    /// <summary>El lugar de cada asiento al terminar: primero el que se queda con todo y despues los eliminados, del ultimo al primero en irse.</summary>
    internal static List<int> Ranking(PokerState s)
    {
        var champion = Enumerable.Range(0, s.Seats).Where(i => s.Players[i].Stack > 0).OrderByDescending(i => s.Players[i].Stack).ToList();
        return [.. champion, .. Enumerable.Reverse(s.Eliminated)];
    }

    /// <summary>El reparto del premio segun cuantos juegan: 2, todo al primero; 3 o 4, 65 % y 35 %; 5 o 6, 50 %, 30 % y 20 %. El resto del redondeo va al primero.</summary>
    internal static List<long> Prizes(long pool, int seats)
    {
        int[] percents = seats <= 2 ? [100] : seats <= 4 ? [65, 35] : [50, 30, 20];
        var prizes = percents.Select(p => pool * p / 100).ToList();
        prizes[0] += pool - prizes.Sum();
        return prizes;
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
