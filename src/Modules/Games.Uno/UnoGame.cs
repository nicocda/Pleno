using System.Text.Json;
using Casino.Modules.Games.Platform;
using Casino.Modules.Games.Tables;

namespace Casino.Modules.Games.Uno;

/// <summary>Algo que paso en la partida (para que el navegador lo cuente: "el asiento 2 jugo un +2").</summary>
public sealed record UnoEvent(int Seat, string Kind, int? Card = null, int? Color = null, int? Count = null);

/// <summary>El estado completo de una partida de Uno. Lo guarda la plataforma como JSON; solo <see cref="UnoGame.View"/> decide que se le muestra a cada jugador.</summary>
public sealed class UnoState
{
    public int Seats { get; set; }

    public long BuyIn { get; set; }

    public List<List<int>> Hands { get; set; } = [];

    /// <summary>El mazo para robar: la carta de arriba es la ultima.</summary>
    public List<int> Draw { get; set; } = [];

    /// <summary>Las cartas jugadas: la de arriba es la ultima.</summary>
    public List<int> Discard { get; set; } = [];

    /// <summary>El color que manda ahora (el de la carta de arriba, o el que eligio quien jugo un comodin).</summary>
    public int TopColor { get; set; }

    public int Current { get; set; }

    public int Direction { get; set; } = 1;

    /// <summary>La carta que acaba de robar quien tiene el turno, si se puede jugar (la juega o pasa).</summary>
    public int? DrawnCard { get; set; }

    public int Winner { get; set; } = -1;

    /// <summary>La semilla y la mesa: hacen falta para rebarajar el descarte de forma verificable cuando se acaba el mazo.</summary>
    public string Seed { get; set; } = string.Empty;

    public string TableId { get; set; } = string.Empty;

    public int Reshuffles { get; set; }

    public List<UnoEvent> Events { get; set; } = [];
}

public sealed record UnoOpponent(int Seat, int Cards);

/// <summary>Lo que ve un asiento: su mano, cuantas cartas tiene cada uno, la carta de arriba y que puede jugar. Nunca las manos ni el mazo de los demas.</summary>
public sealed record UnoView(
    int? You,
    IReadOnlyList<int> Hand,
    IReadOnlyList<int> Playable,
    IReadOnlyList<UnoOpponent> Players,
    int Top,
    int TopColor,
    int DrawCount,
    int Current,
    int Direction,
    int? DrawnCard,
    int Winner,
    IReadOnlyList<UnoEvent> Events);

/// <summary>
/// Uno entre 2 y 6 jugadores (humanos o bots). Gana quien se queda sin cartas y se lleva todo lo que se puso en la mesa. Reglas: se juega una carta del mismo color o
/// numero/tipo que la de arriba, o un comodin; salto, reversa (con 2 jugadores es otro salto), +2 y comodin +4 (que solo se puede jugar sin cartas del color
/// que manda). Sin cartas para jugar se roba una: si se puede jugar se juega o se pasa. No se apilan los +2/+4 y el "Uno!" es automatico.
/// </summary>
public sealed class UnoGame : ITableGame
{
    private const int HandSize = 7;
    private const double TurnSeconds = 30;
    private const int MaxEvents = 8;

    public GameInfo Info { get; } = new("uno", "Uno", "Quedate sin cartas antes que los demas. Mesas de 2 a 6 con amigos o bots.", "/uno", "U");

    public int MinPlayers => 2;

    public int MaxPlayers => 6;

    public long MinBuyIn => 10;

    public long MaxBuyIn => 1_000;

    public string Start(GameStartContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var label = $"uno:{context.TableId:N}";
        var deck = UnoCards.NewDeck();
        FairShuffle.Shuffle(deck, context.Seed, label);

        var state = new UnoState { Seats = context.Seats, BuyIn = context.BuyIn, Seed = context.Seed, TableId = context.TableId.ToString("N") };
        for (var seat = 0; seat < context.Seats; seat++)
        {
            state.Hands.Add([]);
        }

        for (var round = 0; round < HandSize; round++)
        {
            for (var seat = 0; seat < context.Seats; seat++)
            {
                state.Hands[seat].Add(PopTop(deck));
            }
        }

        // La primera carta del descarte no puede ser un comodin: se manda al fondo del mazo y se da vuelta otra.
        var first = PopTop(deck);
        while (UnoCards.IsWild(first))
        {
            deck.Insert(0, first);
            first = PopTop(deck);
        }

        state.Draw = deck;
        state.Discard = [first];
        state.TopColor = UnoCards.ColorOf(first);
        state.Current = FairShuffle.Pick(context.Seed, $"{label}:first", context.Seats);
        state.Events.Add(new UnoEvent(state.Current, "start", first, state.TopColor));

        // Si la carta inicial es de accion, se aplica como si la hubiera jugado el que reparte.
        switch (UnoCards.KindOf(first))
        {
            case UnoKind.Skip:
                state.Current = Next(state, state.Current);
                break;
            case UnoKind.Reverse:
                state.Direction = -1;
                if (state.Seats == 2)
                {
                    state.Current = Next(state, state.Current);
                }

                break;
            case UnoKind.DrawTwo:
                Penalize(state, state.Current, 2);
                state.Current = Next(state, state.Current);
                break;
            default:
                break;
        }

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

        var (type, card, color) = ParseAction(action);
        switch (type)
        {
            case "draw":
                Draw(s, seat);
                break;
            case "pass":
                if (s.DrawnCard is null)
                {
                    throw new TableRuleException("cannot_pass", "Solo podes pasar despues de robar una carta.");
                }

                s.DrawnCard = null;
                s.Events.Add(new UnoEvent(seat, "pass"));
                s.Current = Next(s, seat);
                break;
            case "play":
                Play(s, seat, card ?? throw new TableRuleException("bad_action", "Falta la carta."), color);
                break;
            default:
                throw new TableRuleException("bad_action", "La jugada no existe: usa play, draw o pass.");
        }

        TrimEvents(s);
        return Serialize(s);
    }

    public string TimeoutAction(string state, int seat) => Deserialize(state).DrawnCard is null ? """{"type":"draw"}""" : """{"type":"pass"}""";

    public string BotAction(string state, int seat)
    {
        var s = Deserialize(state);
        var hand = s.Hands[seat];
        if (s.DrawnCard is { } drawn)
        {
            return IsPlayable(s, hand, drawn) ? PlayJson(s, hand, drawn) : """{"type":"pass"}""";
        }

        var playable = hand.Where(c => IsPlayable(s, hand, c)).ToList();
        if (playable.Count == 0)
        {
            return """{"type":"draw"}""";
        }

        // Primero las cartas de color (las de accion antes que las comunes, y las altas antes que las bajas) y los comodines al final.
        var best = playable.OrderBy(c => UnoCards.IsWild(c) ? 1 : 0).ThenByDescending(c => UnoCards.KindOf(c) == UnoKind.Number ? 0 : 1).ThenByDescending(UnoCards.WeightOf).First();
        return PlayJson(s, hand, best);
    }

    public object View(string state, int? seat)
    {
        var s = Deserialize(state);
        var hand = seat is { } me ? s.Hands[me] : [];
        var myTurn = seat is { } who && who == s.Current && s.Winner < 0;
        var playable = myTurn ? hand.Where(c => (s.DrawnCard is null || s.DrawnCard == c) && IsPlayable(s, hand, c)).ToList() : [];
        return new UnoView(
            seat,
            [.. hand],
            playable,
            [.. s.Hands.Select((h, i) => new UnoOpponent(i, h.Count))],
            s.Discard[^1],
            s.TopColor,
            s.Draw.Count,
            s.Current,
            s.Direction,
            myTurn ? s.DrawnCard : null,
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

    // ---- Reglas ----

    private static void Draw(UnoState s, int seat)
    {
        if (s.DrawnCard is not null)
        {
            throw new TableRuleException("already_drew", "Ya robaste: juga esa carta o pasa.");
        }

        var card = DrawCard(s);
        if (card is null)
        {
            s.Events.Add(new UnoEvent(seat, "draw", Count: 0)); // no queda ninguna carta para robar
            s.Current = Next(s, seat);
            return;
        }

        s.Hands[seat].Add(card.Value);
        s.Events.Add(new UnoEvent(seat, "draw", Count: 1));
        if (IsPlayable(s, s.Hands[seat], card.Value))
        {
            s.DrawnCard = card.Value; // la puede jugar o pasar
        }
        else
        {
            s.Current = Next(s, seat);
        }
    }

    private static void Play(UnoState s, int seat, int card, int? color)
    {
        var hand = s.Hands[seat];
        if (!UnoCards.IsValid(card) || !hand.Contains(card))
        {
            throw new TableRuleException("not_in_hand", "Esa carta no esta en tu mano.");
        }

        if (s.DrawnCard is { } drawn && drawn != card)
        {
            throw new TableRuleException("drawn_only", "Despues de robar solo podes jugar esa carta o pasar.");
        }

        if (!IsPlayable(s, hand, card))
        {
            throw new TableRuleException("illegal", UnoCards.KindOf(card) == UnoKind.WildDrawFour
                ? "El +4 solo se puede jugar si no tenes ninguna carta del color que manda."
                : "Esa carta no se puede jugar sobre la de arriba.");
        }

        var wild = UnoCards.IsWild(card);
        if (wild && color is not (>= 0 and < UnoCards.Colors))
        {
            throw new TableRuleException("color_required", "Elegi el color (0 rojo, 1 amarillo, 2 verde, 3 azul).");
        }

        hand.Remove(card);
        s.Discard.Add(card);
        s.TopColor = wild ? color!.Value : UnoCards.ColorOf(card);
        s.DrawnCard = null;
        s.Events.Add(new UnoEvent(seat, "play", card, s.TopColor));

        if (hand.Count == 0)
        {
            s.Winner = seat;
            return;
        }

        switch (UnoCards.KindOf(card))
        {
            case UnoKind.Skip:
                s.Events.Add(new UnoEvent(Next(s, seat), "skipped"));
                s.Current = Next(s, Next(s, seat));
                break;
            case UnoKind.Reverse:
                s.Direction = -s.Direction;
                s.Current = s.Seats == 2 ? seat : Next(s, seat); // con 2 jugadores la reversa es otro salto: vuelve a jugar el mismo
                break;
            case UnoKind.DrawTwo:
                var two = Next(s, seat);
                Penalize(s, two, 2);
                s.Current = Next(s, two);
                break;
            case UnoKind.WildDrawFour:
                var four = Next(s, seat);
                Penalize(s, four, 4);
                s.Current = Next(s, four);
                break;
            default:
                s.Current = Next(s, seat);
                break;
        }
    }

    /// <summary>Un comodin se juega siempre; un +4 solo sin cartas del color que manda; el resto, por color o por numero/tipo.</summary>
    internal static bool IsPlayable(UnoState s, List<int> hand, int card)
    {
        switch (UnoCards.KindOf(card))
        {
            case UnoKind.Wild:
                return true;
            case UnoKind.WildDrawFour:
                return !hand.Any(c => c != card && !UnoCards.IsWild(c) && UnoCards.ColorOf(c) == s.TopColor);
        }

        if (UnoCards.ColorOf(card) == s.TopColor)
        {
            return true;
        }

        var top = s.Discard[^1];
        var kind = UnoCards.KindOf(card);
        return kind == UnoCards.KindOf(top) && (kind != UnoKind.Number || UnoCards.NumberOf(card) == UnoCards.NumberOf(top));
    }

    private static void Penalize(UnoState s, int seat, int count)
    {
        var drawn = 0;
        for (var i = 0; i < count; i++)
        {
            if (DrawCard(s) is { } card)
            {
                s.Hands[seat].Add(card);
                drawn++;
            }
        }

        s.Events.Add(new UnoEvent(seat, "penalty", Count: drawn));
    }

    /// <summary>Roba la carta de arriba; si el mazo se acabo, se rebaraja el descarte (menos la carta de arriba) de forma verificable.</summary>
    private static int? DrawCard(UnoState s)
    {
        if (s.Draw.Count == 0)
        {
            if (s.Discard.Count <= 1)
            {
                return null;
            }

            var top = s.Discard[^1];
            var rest = s.Discard.Take(s.Discard.Count - 1).ToList();
            FairShuffle.Shuffle(rest, s.Seed, $"uno:{s.TableId}:r{s.Reshuffles++}");
            s.Draw = rest;
            s.Discard = [top];
        }

        return PopTop(s.Draw);
    }

    private static int Next(UnoState s, int seat) => (seat + s.Direction + s.Seats) % s.Seats;

    private static int PopTop(List<int> pile)
    {
        var card = pile[^1];
        pile.RemoveAt(pile.Count - 1);
        return card;
    }

    private static void TrimEvents(UnoState s)
    {
        if (s.Events.Count > MaxEvents)
        {
            s.Events.RemoveRange(0, s.Events.Count - MaxEvents);
        }
    }

    // ---- Formato ----

    private static string PlayJson(UnoState s, List<int> hand, int card)
    {
        if (!UnoCards.IsWild(card))
        {
            return $$"""{"type":"play","card":{{card}}}""";
        }

        // Para un comodin se elige el color que mas cartas tiene en la mano (si no tiene de color, el que ya manda).
        var color = hand.Where(c => !UnoCards.IsWild(c)).GroupBy(UnoCards.ColorOf).OrderByDescending(g => g.Count()).Select(g => g.Key).DefaultIfEmpty(s.TopColor).First();
        return $$"""{"type":"play","card":{{card}},"color":{{color}}}""";
    }

    private static (string Type, int? Card, int? Color) ParseAction(string action)
    {
        try
        {
            using var doc = JsonDocument.Parse(action);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString() ?? string.Empty;
            int? card = root.TryGetProperty("card", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null;
            int? color = root.TryGetProperty("color", out var k) && k.ValueKind == JsonValueKind.Number ? k.GetInt32() : null;
            return (type, card, color);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new TableRuleException("bad_action", "La jugada no se entiende.");
        }
    }

    private static string Serialize(UnoState state) => JsonSerializer.Serialize(state, JsonOptions);

    internal static UnoState Deserialize(string state) => JsonSerializer.Deserialize<UnoState>(state, JsonOptions)!;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
