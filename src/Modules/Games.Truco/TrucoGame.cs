using System.Text.Json;
using Casino.Modules.Games.Platform;
using Casino.Modules.Games.Tables;

namespace Casino.Modules.Games.Truco;

/// <summary>Una carta jugada en la mesa: quien la jugo.</summary>
public sealed record TrucoPlay(int Seat, int Card);

/// <summary>Algo que paso (para que el navegador lo cuente: "Jugador 2 canto truco"). En el resultado de un envido, A y B son los puntos de cada asiento.</summary>
public sealed record TrucoEvent(int Seat, string Kind, int? Card = null, int? Value = null, int? A = null, int? B = null);

/// <summary>Un canto que espera respuesta: un truco (con el nivel que se propone) o un envido (con la cadena de cantos).</summary>
public sealed class TrucoPending
{
    public string Kind { get; set; } = string.Empty;

    /// <summary>Quien hizo el ultimo canto (el otro asiento tiene que responder).</summary>
    public int Caller { get; set; }

    /// <summary>Quien tenia el turno cuando empezaron los cantos: cuando se resuelven, el turno vuelve a esa persona para que siga con su jugada.</summary>
    public int Origin { get; set; }

    /// <summary>Solo en el truco: 1 truco, 2 retruco, 3 vale cuatro.</summary>
    public int Level { get; set; }

    /// <summary>Solo en el envido: los cantos en orden (<c>envido</c>, <c>real_envido</c>, <c>falta_envido</c>).</summary>
    public List<string> Calls { get; set; } = [];
}

/// <summary>El estado completo de una partida de Truco. Lo guarda la plataforma como JSON; solo <see cref="TrucoGame.View"/> decide que se le muestra a cada jugador.</summary>
public sealed class TrucoState
{
    public int Seats { get; set; } = 2;

    public long BuyIn { get; set; }

    /// <summary>Con cuantos puntos se gana la partida.</summary>
    public int Target { get; set; } = TrucoGame.TargetPoints;

    public List<int> Scores { get; set; } = [0, 0];

    /// <summary>Quien es mano en esta ronda (juega primero y desempata): se alterna en cada ronda.</summary>
    public int Mano { get; set; }

    public int HandNo { get; set; }

    public int Current { get; set; }

    /// <summary>Las tres cartas repartidas a cada uno (no cambian: el envido se calcula con ellas aunque ya las hayas jugado).</summary>
    public List<List<int>> Dealt { get; set; } = [];

    /// <summary>Las cartas que le quedan a cada uno.</summary>
    public List<List<int>> Hands { get; set; } = [];

    /// <summary>Las cartas jugadas en esta ronda, en orden.</summary>
    public List<TrucoPlay> Table { get; set; } = [];

    /// <summary>Quien gano cada baza (mano) de esta ronda: el asiento, o -1 si fue parda (empate).</summary>
    public List<int> Bazas { get; set; } = [];

    /// <summary>0 nada, 1 truco, 2 retruco, 3 vale cuatro (lo que ya se acepto).</summary>
    public int TrucoLevel { get; set; }

    /// <summary>Quien hizo el ultimo canto de truco aceptado (-1 si ninguno): el otro es el unico que puede subirlo.</summary>
    public int TrucoCaller { get; set; } = -1;

    public bool EnvidoDone { get; set; }

    public TrucoPending? Pending { get; set; }

    public int Winner { get; set; } = -1;

    public string Seed { get; set; } = string.Empty;

    public string TableId { get; set; } = string.Empty;

    public List<TrucoEvent> Events { get; set; } = [];
}

public sealed record TrucoPendingView(string Kind, int Caller, int Level, IReadOnlyList<string> Calls);

/// <summary>Lo que ve un asiento: sus cartas y sus puntos de envido, las cartas ya jugadas, el marcador y que puede hacer ahora. Nunca las cartas del otro.</summary>
public sealed record TrucoView(
    int? You,
    IReadOnlyList<int> Hand,
    int? EnvidoPoints,
    int OpponentCards,
    IReadOnlyList<TrucoPlay> Table,
    IReadOnlyList<int> Bazas,
    IReadOnlyList<int> Scores,
    int Target,
    int Mano,
    int HandNo,
    int Current,
    int TrucoLevel,
    TrucoPendingView? Pending,
    IReadOnlyList<string> Actions,
    int Winner,
    IReadOnlyList<TrucoEvent> Events);

/// <summary>
/// Truco argentino de 2 jugadores con baraja espanola de 40 cartas, a 15 puntos (sin flor). Cada ronda se reparten 3 cartas y se juegan hasta 3 bazas; se canta
/// <b>envido</b> (en la primera baza: envido, real envido y falta envido, con sus subidas) y <b>truco</b> (truco, retruco y vale cuatro) y se puede ir <b>al mazo</b>.
/// Quien llega primero a 15 se lleva todo lo que se puso en la mesa.
/// </summary>
public sealed class TrucoGame : ITableGame
{
    public const int TargetPoints = 15;
    private const double TurnSeconds = 45;
    private const int MaxEvents = 14;

    public GameInfo Info { get; } = new("truco", "Truco", "Truco argentino a dos: envido, truco, retruco y vale cuatro. El primero a 15 se lleva todo.", "/truco", "T");

    public int MinPlayers => 2;

    public int MaxPlayers => 2;

    public long MinBuyIn => 10;

    public long MaxBuyIn => 1_000;

    public string Start(GameStartContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var state = new TrucoState { BuyIn = context.BuyIn, Seed = context.Seed, TableId = context.TableId.ToString("N") };
        state.Mano = FairShuffle.Pick(context.Seed, $"truco:{state.TableId}:mano", 2);
        state.HandNo = 0;
        NewHand(state, firstHand: true);
        return Serialize(state);
    }

    public TurnInfo? Turn(string state)
    {
        var s = Deserialize(state);
        return s.Winner >= 0 ? null : new TurnInfo(s.Current, TurnSeconds);
    }

    public string Apply(string state, int seat, string action, DateTimeOffset now)
    {
        var s = Deserialize(state);
        if (s.Winner >= 0)
        {
            throw new TableRuleException("finished", "La partida ya termino.");
        }

        if (seat != s.Current)
        {
            throw new TableRuleException("not_your_turn", "No es tu turno.");
        }

        var (type, card) = ParseAction(action);
        var legal = Legal(s, seat);
        if (!legal.Contains(type))
        {
            throw new TableRuleException("illegal", $"No podes hacer eso ahora ({Explain(s, type)}).");
        }

        switch (type)
        {
            case "play":
                PlayCard(s, seat, card ?? throw new TableRuleException("bad_action", "Falta la carta."));
                break;
            case "truco" or "retruco" or "vale4" or "envido" or "real_envido" or "falta_envido":
                RaiseOrCall(s, seat, type);
                break;
            case "quiero":
                Accept(s);
                break;
            case "no_quiero":
                Reject(s);
                break;
            case "mazo":
                Fold(s, seat);
                break;
            default:
                throw new TableRuleException("bad_action", "La jugada no existe.");
        }

        Trim(s);
        return Serialize(s);
    }

    public string TimeoutAction(string state, int seat)
    {
        var s = Deserialize(state);
        if (s.Pending is not null)
        {
            return """{"type":"no_quiero"}""";
        }

        // Si no juega a tiempo, tira su carta mas baja.
        var lowest = s.Hands[seat].OrderBy(TrucoCards.StrengthOf).First();
        return $$"""{"type":"play","card":{{lowest}}}""";
    }

    public string BotAction(string state, int seat) => TrucoBot.Choose(Deserialize(state), seat);

    public object View(string state, int? seat)
    {
        var s = Deserialize(state);
        var mine = seat is { } me ? s.Hands[me] : [];
        var actions = seat is { } who && who == s.Current ? Legal(s, who).OrderBy(a => a, StringComparer.Ordinal).ToList() : [];
        return new TrucoView(
            seat,
            [.. mine],
            seat is { } p ? TrucoCards.EnvidoPoints(s.Dealt[p]) : null,
            seat is { } q ? s.Hands[1 - q].Count : s.Hands.Sum(h => h.Count),
            s.Table,
            s.Bazas,
            s.Scores,
            s.Target,
            s.Mano,
            s.HandNo,
            s.Current,
            s.TrucoLevel,
            s.Pending is null ? null : new TrucoPendingView(s.Pending.Kind, s.Pending.Caller, s.Pending.Level, s.Pending.Calls),
            actions,
            s.Winner,
            s.Events);
    }

    public GameOutcome? Outcome(string state)
    {
        var s = Deserialize(state);
        if (s.Winner < 0)
        {
            return null;
        }

        var payouts = new long[s.Seats];
        payouts[s.Winner] = s.BuyIn * s.Seats;
        return new GameOutcome(payouts);
    }

    // ---- Lo que se puede hacer ----

    /// <summary>Las jugadas legales de un asiento en este momento.</summary>
    internal static HashSet<string> Legal(TrucoState s, int seat)
    {
        var legal = new HashSet<string>(StringComparer.Ordinal);
        if (s.Winner >= 0 || seat != s.Current)
        {
            return legal;
        }

        if (s.Pending is { } pending)
        {
            legal.Add("quiero");
            legal.Add("no_quiero");
            if (pending.Kind == "truco")
            {
                if (pending.Level == 1)
                {
                    legal.Add("retruco");
                }
                else if (pending.Level == 2)
                {
                    legal.Add("vale4");
                }
            }
            else
            {
                legal.UnionWith(EnvidoRaises(pending.Calls));
            }

            return legal;
        }

        if (s.Hands[seat].Count > 0)
        {
            legal.Add("play");
        }

        legal.Add("mazo");
        if (s.TrucoLevel < 3 && (s.TrucoLevel == 0 || s.TrucoCaller != seat))
        {
            legal.Add(s.TrucoLevel switch { 0 => "truco", 1 => "retruco", _ => "vale4" });
        }

        if (EnvidoAvailable(s))
        {
            legal.UnionWith(EnvidoRaises([]));
        }

        return legal;
    }

    /// <summary>El envido solo se canta en la primera baza, antes de aceptar un truco y si todavia no se jugo.</summary>
    private static bool EnvidoAvailable(TrucoState s) => s.Bazas.Count == 0 && !s.EnvidoDone && s.TrucoLevel == 0;

    /// <summary>Que se puede cantar encima de lo que ya se canto: envido hasta dos veces, real envido y falta envido (y despues de la falta, nada).</summary>
    private static IEnumerable<string> EnvidoRaises(IReadOnlyList<string> calls)
    {
        if (!calls.Contains("real_envido") && !calls.Contains("falta_envido") && calls.Count(c => c == "envido") < 2)
        {
            yield return "envido";
        }

        if (!calls.Contains("real_envido") && !calls.Contains("falta_envido"))
        {
            yield return "real_envido";
        }

        if (!calls.Contains("falta_envido"))
        {
            yield return "falta_envido";
        }
    }

    private static string Explain(TrucoState s, string type) =>
        s.Pending is not null ? "hay un canto esperando tu respuesta" : type is "envido" or "real_envido" or "falta_envido" ? "el envido solo se canta en la primera baza" : "ahora no corresponde";

    // ---- Jugar una carta ----

    private static void PlayCard(TrucoState s, int seat, int card)
    {
        if (!s.Hands[seat].Contains(card))
        {
            throw new TableRuleException("not_in_hand", "Esa carta no esta en tu mano.");
        }

        s.Hands[seat].Remove(card);
        s.Table.Add(new TrucoPlay(seat, card));
        s.Events.Add(new TrucoEvent(seat, "play", card));

        if (s.Table.Count % 2 == 1)
        {
            s.Current = 1 - seat; // le toca al otro responder la carta
            return;
        }

        // Las dos cartas de la baza estan en la mesa: gana la mas fuerte (o es parda).
        var first = s.Table[^2];
        var second = s.Table[^1];
        var a = TrucoCards.StrengthOf(first.Card);
        var b = TrucoCards.StrengthOf(second.Card);
        var winner = a == b ? -1 : a > b ? first.Seat : second.Seat;
        s.Bazas.Add(winner);
        s.Events.Add(new TrucoEvent(winner, winner < 0 ? "parda" : "baza"));

        if (HandWinner(s.Bazas, s.Mano) is { } handWinner)
        {
            EndHand(s, handWinner, HandPoints(s));
            return;
        }

        // Juega primero quien gano la baza (si fue parda, el mano).
        s.Current = winner < 0 ? s.Mano : winner;
    }

    /// <summary>Quien gano la ronda segun las bazas, o null si todavia no se definio. Gana el que lleva dos; si la primera fue parda decide la segunda; si la segunda fue parda, la primera.</summary>
    public static int? HandWinner(IReadOnlyList<int> bazas, int mano)
    {
        var wins0 = bazas.Count(w => w == 0);
        var wins1 = bazas.Count(w => w == 1);
        if (wins0 >= 2)
        {
            return 0;
        }

        if (wins1 >= 2)
        {
            return 1;
        }

        switch (bazas.Count)
        {
            case 2 when bazas[0] == -1 && bazas[1] != -1:
                return bazas[1];
            case 2 when bazas[0] != -1 && bazas[1] == -1:
                return bazas[0];
            case 3:
                // 1 a 1 (o dos pardas): decide la tercera; si tambien fue parda, la primera baza y, si no hubo, el mano.
                if (bazas[2] != -1)
                {
                    return bazas[2];
                }

                return bazas[0] != -1 ? bazas[0] : mano;
            default:
                return null;
        }
    }

    private static int HandPoints(TrucoState s) => s.TrucoLevel + 1;

    // ---- Cantos ----

    private static void CallTruco(TrucoState s, int seat, string type)
    {
        s.Pending = new TrucoPending { Kind = "truco", Caller = seat, Origin = seat, Level = s.TrucoLevel + 1 };
        s.Events.Add(new TrucoEvent(seat, type, Value: s.Pending.Level + 1));
        s.Current = 1 - seat;
    }

    private static void CallEnvido(TrucoState s, int seat, string type)
    {
        s.Pending = new TrucoPending { Kind = "envido", Caller = seat, Origin = seat, Calls = [type] };
        s.Events.Add(new TrucoEvent(seat, type));
        s.Current = 1 - seat;
    }

    /// <summary>Quiero: se acepta el canto. Un envido se resuelve en el momento; un truco sube lo que vale la ronda.</summary>
    private static void Accept(TrucoState s)
    {
        var pending = s.Pending!;
        var responder = 1 - pending.Caller;
        s.Events.Add(new TrucoEvent(responder, "quiero"));
        s.Pending = null;
        s.Current = pending.Origin; // el que tenia el turno sigue con su jugada

        if (pending.Kind == "truco")
        {
            s.TrucoLevel = pending.Level;
            s.TrucoCaller = pending.Caller;
            return;
        }

        s.EnvidoDone = true;
        var points = new[] { TrucoCards.EnvidoPoints(s.Dealt[0]), TrucoCards.EnvidoPoints(s.Dealt[1]) };
        var winner = points[0] == points[1] ? s.Mano : points[0] > points[1] ? 0 : 1;
        var value = EnvidoValue(s, pending.Calls);
        s.Events.Add(new TrucoEvent(winner, "envido_result", Value: value, A: points[0], B: points[1]));
        Award(s, winner, value);
    }

    /// <summary>No quiero: el que canto se lleva lo que valia antes del ultimo canto (al menos 1) en el envido, o la ronda entera en el truco.</summary>
    private static void Reject(TrucoState s)
    {
        var pending = s.Pending!;
        var responder = 1 - pending.Caller;
        s.Events.Add(new TrucoEvent(responder, "no_quiero"));
        s.Pending = null;
        s.Current = pending.Origin;

        if (pending.Kind == "truco")
        {
            EndHand(s, pending.Caller, pending.Level); // sin aceptar el nivel nuevo, se lleva lo que valia el anterior (1, 2 o 3)
            return;
        }

        s.EnvidoDone = true;
        var accepted = pending.Calls.Take(pending.Calls.Count - 1).Sum(c => c == "envido" ? 2 : 3);
        Award(s, pending.Caller, Math.Max(1, accepted));
    }

    /// <summary>
    /// Subir un canto es aceptar el anterior y cantar otro: la respuesta a un envido puede ser otro canto de envido; la respuesta a un truco puede ser retruco (o vale
    /// cuatro). Quien responde pasa a ser el que canto.
    /// </summary>
    private static void CallRaise(TrucoState s, int seat, string type)
    {
        var pending = s.Pending!;
        if (pending.Kind == "truco")
        {
            s.TrucoLevel = pending.Level; // acepta el truco que le cantaron
            s.TrucoCaller = pending.Caller;
            s.Pending = new TrucoPending { Kind = "truco", Caller = seat, Origin = pending.Origin, Level = pending.Level + 1 };
            s.Events.Add(new TrucoEvent(seat, type, Value: pending.Level + 2));
        }
        else
        {
            pending.Calls.Add(type);
            pending.Caller = seat;
            s.Events.Add(new TrucoEvent(seat, type));
        }

        s.Current = 1 - seat;
    }

    private static int EnvidoValue(TrucoState s, IReadOnlyList<string> calls)
    {
        if (calls.Contains("falta_envido"))
        {
            return Math.Max(1, s.Target - s.Scores.Max()); // lo que le falta al que va ganando para terminar la partida
        }

        return calls.Sum(c => c == "envido" ? 2 : 3);
    }

    private static void Fold(TrucoState s, int seat)
    {
        s.Events.Add(new TrucoEvent(seat, "mazo"));
        EndHand(s, 1 - seat, HandPoints(s));
    }

    // ---- Puntos y rondas ----

    /// <summary>Suma puntos (del envido) y termina la partida si alguien llego al objetivo. No termina la ronda.</summary>
    private static void Award(TrucoState s, int seat, int points)
    {
        s.Scores[seat] += points;
        if (s.Scores[seat] >= s.Target)
        {
            s.Winner = seat;
        }
    }

    private static void EndHand(TrucoState s, int winner, int points)
    {
        s.Events.Add(new TrucoEvent(winner, "hand_end", Value: points));
        Award(s, winner, points);
        if (s.Winner < 0)
        {
            s.Mano = 1 - s.Mano;
            NewHand(s, firstHand: false);
        }
    }

    private static void NewHand(TrucoState s, bool firstHand)
    {
        s.HandNo++;
        var deck = TrucoCards.NewDeck();
        FairShuffle.Shuffle(deck, s.Seed, $"truco:{s.TableId}:h{s.HandNo}");
        s.Dealt = [[deck[0], deck[2], deck[4]], [deck[1], deck[3], deck[5]]];
        s.Hands = [[.. s.Dealt[0]], [.. s.Dealt[1]]];
        s.Table = [];
        s.Bazas = [];
        s.TrucoLevel = 0;
        s.TrucoCaller = -1;
        s.EnvidoDone = false;
        s.Pending = null;
        s.Current = s.Mano;
        if (firstHand)
        {
            s.Events.Add(new TrucoEvent(s.Mano, "start"));
        }
    }

    private static void Trim(TrucoState s)
    {
        if (s.Events.Count > MaxEvents)
        {
            s.Events.RemoveRange(0, s.Events.Count - MaxEvents);
        }
    }

    // ---- Formato ----

    private static (string Type, int? Card) ParseAction(string action)
    {
        try
        {
            using var doc = JsonDocument.Parse(action);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString() ?? string.Empty;
            int? card = root.TryGetProperty("card", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null;
            return (type, card);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new TableRuleException("bad_action", "La jugada no se entiende.");
        }
    }

    private static string Serialize(TrucoState state) => JsonSerializer.Serialize(state, JsonOptions);

    internal static TrucoState Deserialize(string state) => JsonSerializer.Deserialize<TrucoState>(state, JsonOptions)!;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Un canto: si hay algo pendiente es una subida (acepta el anterior y canta otro); si no, es un canto nuevo.</summary>
    private static void RaiseOrCall(TrucoState s, int seat, string type)
    {
        if (s.Pending is not null)
        {
            CallRaise(s, seat, type);
        }
        else if (type is "truco" or "retruco" or "vale4")
        {
            CallTruco(s, seat, type);
        }
        else
        {
            CallEnvido(s, seat, type);
        }
    }
}
