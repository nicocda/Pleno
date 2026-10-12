using System.Text.Json;
using Casino.Modules.Games.Fairness;
using Casino.Modules.Games.Poker;
using Casino.Modules.Games.Tables;

namespace Casino.Games.Tests;

/// <summary>Las reglas de Texas Hold'em (maquina de estados pura): evaluacion de manos, ciegas, rondas de apuestas, showdown, pagos y manos completas de bots contra bots.</summary>
public sealed class PokerTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly PokerGame Game = new();
    private static readonly Guid TableId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly int[] HandsChecked = [1, 10, 11, 20, 21, 31, 41];
    private static readonly int[] SeatCounts = [2, 3, 4, 5, 6];

    private const int Spades = 0;
    private const int Hearts = 1;
    private const int Diamonds = 2;
    private const int Clubs = 3;

    private static int C(int suit, int rank) => (suit * 13) + (rank - 2); // rango 2..14 (14 = as)

    private static string StartHand(int seats, long buyIn = 100, string seed = "poker-test-seed") =>
        Game.Start(new GameStartContext(seats, seed, TableId, buyIn, DateTimeOffset.UnixEpoch));

    private static PokerState Parse(string state) => JsonSerializer.Deserialize<PokerState>(state, Json)!;

    private static string Serialize(PokerState state) => JsonSerializer.Serialize(state, Json);

    private static string Do(string state, string type, long? to = null)
    {
        var seat = Parse(state).Current;
        return Game.Apply(state, seat, to is null ? $$"""{"type":"{{type}}"}""" : $$"""{"type":"{{type}}","to":{{to}}}""", DateTimeOffset.UnixEpoch);
    }

    /// <summary>Una mano con cartas elegidas: las de cada asiento y las cinco comunitarias (flop, turn y river), en ese orden.</summary>
    private static string Crafted(int seats, int[][] hole, int[] board, long buyIn = 100)
    {
        var state = Parse(StartHand(seats, buyIn));
        for (var i = 0; i < seats; i++)
        {
            state.Players[i].Cards = [.. hole[i]];
        }

        state.Deck = [.. board, .. state.Deck.Where(c => !board.Contains(c) && !hole.SelectMany(h => h).Contains(c))];
        return Serialize(state);
    }

    /// <summary>Las fichas con las que arranco cada asiento la mano (antes de las ciegas): arma pilas desiguales.</summary>
    private static string WithStartingStacks(string state, params long[] starting)
    {
        var s = Parse(state);
        for (var i = 0; i < starting.Length; i++)
        {
            s.Players[i].Stack = starting[i] - s.Players[i].Bet;
        }

        return Serialize(s);
    }

    /// <summary>Le da el turno a un asiento (para controlar el orden de los all-in sin depender de donde cae el boton).</summary>
    private static string WithTurn(string state, int seat)
    {
        var s = Parse(state);
        s.Current = seat;
        return Serialize(s);
    }

    /// <summary>Todo el mundo va all-in (o iguala si ya no puede subir) hasta que la mano termina.</summary>
    private static string ShoveUntilDone(string state)
    {
        var guard = 0;
        while (!Parse(state).Done)
        {
            Assert.True(++guard < 20, "la mano no termina");
            var s = Parse(state);
            state = PokerGame.CanRaise(s, s.Current) ? Do(state, "raise", PokerGame.RaiseLimits(s, s.Current).MaxTo) : Do(state, "call");
        }

        return state;
    }

    private static long[] Stacks(string state) => [.. Parse(state).Players.Select(p => p.Stack)];

    private static long Score(params int[] cards) => PokerCards.Evaluate(cards);

    [Fact]
    public void Hands_are_ranked_by_category_and_then_by_their_kickers()
    {
        var royal = Score(C(Spades, 14), C(Spades, 13), C(Spades, 12), C(Spades, 11), C(Spades, 10));
        var straightFlush = Score(C(Hearts, 9), C(Hearts, 8), C(Hearts, 7), C(Hearts, 6), C(Hearts, 5));
        var four = Score(C(Spades, 9), C(Hearts, 9), C(Diamonds, 9), C(Clubs, 9), C(Spades, 14));
        var full = Score(C(Spades, 9), C(Hearts, 9), C(Diamonds, 9), C(Clubs, 13), C(Spades, 13));
        var flush = Score(C(Spades, 14), C(Spades, 10), C(Spades, 8), C(Spades, 6), C(Spades, 2));
        var straight = Score(C(Spades, 10), C(Hearts, 9), C(Diamonds, 8), C(Clubs, 7), C(Spades, 6));
        var trips = Score(C(Spades, 7), C(Hearts, 7), C(Diamonds, 7), C(Clubs, 13), C(Spades, 2));
        var twoPair = Score(C(Spades, 7), C(Hearts, 7), C(Diamonds, 5), C(Clubs, 5), C(Spades, 13));
        var pair = Score(C(Spades, 7), C(Hearts, 7), C(Diamonds, 13), C(Clubs, 5), C(Spades, 2));
        var high = Score(C(Spades, 14), C(Hearts, 12), C(Diamonds, 9), C(Clubs, 5), C(Spades, 2));

        long[] ranking = [royal, straightFlush, four, full, flush, straight, trips, twoPair, pair, high];
        for (var i = 0; i < ranking.Length - 1; i++)
        {
            Assert.True(ranking[i] > ranking[i + 1], $"la mano {i} deberia ganarle a la {i + 1}");
        }

        Assert.Equal(PokerCategory.StraightFlush, PokerCards.CategoryOf(royal));
        Assert.Equal(PokerCategory.FullHouse, PokerCards.CategoryOf(full));
        Assert.Equal(PokerCategory.HighCard, PokerCards.CategoryOf(high));
    }

    [Fact]
    public void Kickers_break_ties_and_equal_hands_tie()
    {
        Assert.True(Score(C(Spades, 7), C(Hearts, 7), C(Diamonds, 14), C(Clubs, 5), C(Spades, 2)) > Score(C(Clubs, 7), C(Diamonds, 7), C(Spades, 13), C(Hearts, 5), C(Hearts, 2)));
        Assert.True(Score(C(Spades, 9), C(Hearts, 9), C(Diamonds, 9), C(Clubs, 13), C(Spades, 13)) > Score(C(Spades, 8), C(Hearts, 8), C(Diamonds, 8), C(Clubs, 14), C(Spades, 14))); // el trio manda en el full
        Assert.Equal(
            Score(C(Spades, 14), C(Hearts, 12), C(Diamonds, 9), C(Clubs, 5), C(Spades, 2)),
            Score(C(Hearts, 14), C(Spades, 12), C(Clubs, 9), C(Diamonds, 5), C(Hearts, 2))); // los palos no desempatan
    }

    [Fact]
    public void The_wheel_is_the_lowest_straight_and_the_ace_does_not_wrap_around()
    {
        var wheel = Score(C(Spades, 14), C(Hearts, 2), C(Diamonds, 3), C(Clubs, 4), C(Spades, 5));
        var sixHigh = Score(C(Spades, 2), C(Hearts, 3), C(Diamonds, 4), C(Clubs, 5), C(Spades, 6));
        var broadway = Score(C(Spades, 10), C(Hearts, 11), C(Diamonds, 12), C(Clubs, 13), C(Spades, 14));
        var wrap = Score(C(Spades, 12), C(Hearts, 13), C(Diamonds, 14), C(Clubs, 2), C(Spades, 3)); // Q-K-A-2-3 no es escalera

        Assert.Equal(PokerCategory.Straight, PokerCards.CategoryOf(wheel));
        Assert.True(wheel < sixHigh);
        Assert.True(sixHigh < broadway);
        Assert.Equal(PokerCategory.HighCard, PokerCards.CategoryOf(wrap));
    }

    [Fact]
    public void The_best_five_of_seven_cards_is_chosen()
    {
        // Un par en la mano y color en el tablero: gana el color; el 7 de cartas se usa entero.
        var score = PokerCards.Evaluate([C(Spades, 14), C(Hearts, 14), C(Spades, 9), C(Spades, 7), C(Spades, 4), C(Spades, 2), C(Diamonds, 13)]);

        Assert.Equal(PokerCategory.Flush, PokerCards.CategoryOf(score));
        Assert.Equal(PokerCategory.FullHouse, PokerCards.CategoryOf(PokerCards.Evaluate([C(Spades, 8), C(Hearts, 8), C(Diamonds, 8), C(Clubs, 3), C(Spades, 3), C(Hearts, 3), C(Diamonds, 2)]))); // dos tríos: full con el mejor
        Assert.Throws<ArgumentException>(() => PokerCards.Evaluate([1, 2, 3, 4]));
    }

    [Fact]
    public void Starting_posts_the_blinds_deals_two_cards_each_and_is_deterministic()
    {
        var state = Parse(StartHand(4, buyIn: 100));

        Assert.Equal((2L, 4L), (state.SmallBlind, state.BigBlind));
        Assert.All(state.Players, p => Assert.Equal(2, p.Cards.Count));
        Assert.Equal(8, state.Players.SelectMany(p => p.Cards).Distinct().Count());
        Assert.Equal((state.Dealer + 1) % 4, state.SmallBlindSeat); // la ciega chica es la siguiente al boton
        Assert.Equal(2, state.Players[state.SmallBlindSeat].Bet);
        Assert.Equal(4, state.Players[state.BigBlindSeat].Bet);
        Assert.Equal(6, state.Pot);
        Assert.Equal(4, state.CurrentBet);
        Assert.Equal((state.BigBlindSeat + 1) % 4, state.Current); // juega primero el que sigue a la ciega grande
        Assert.Equal(100 - 2, state.Players[state.SmallBlindSeat].Stack);
        Assert.Equal(StartHand(4), StartHand(4));
        Assert.NotEqual(StartHand(4), StartHand(4, seed: "another-seed"));
    }

    [Fact]
    public void Heads_up_the_dealer_posts_the_small_blind_and_acts_first()
    {
        var state = Parse(StartHand(2));

        Assert.Equal(state.Dealer, state.SmallBlindSeat);
        Assert.Equal(state.Dealer, state.Current);
        Assert.Equal(1 - state.Dealer, state.BigBlindSeat);
    }

    [Fact]
    public void The_legal_actions_and_raise_limits_follow_what_there_is_to_call()
    {
        var state = StartHand(3);
        var current = Parse(state).Current;
        var view = (PokerView)Game.View(state, current);

        Assert.Equal(["call", "fold", "raise"], view.Actions);
        Assert.Equal(4, view.ToCall);
        Assert.Equal(8, view.MinRaiseTo); // subir al menos lo que subio la ciega: 4 + 4
        Assert.Equal(100, view.MaxRaiseTo); // todo lo que tiene
        Assert.Empty(((PokerView)Game.View(state, (current + 1) % 3)).Actions); // los demas no pueden actuar
    }

    [Fact]
    public void Calling_and_checking_closes_each_round_and_deals_flop_turn_and_river()
    {
        var state = StartHand(3);
        Assert.Equal("preflop", Parse(state).Street);

        state = Do(state, "call"); // el primero iguala
        state = Do(state, "call"); // la ciega chica completa
        Assert.Equal("preflop", Parse(state).Street); // la ciega grande todavia tiene opcion
        state = Do(state, "check");

        var flop = Parse(state);
        Assert.Equal(("flop", 3, 0L), (flop.Street, flop.Board.Count, flop.CurrentBet));
        Assert.Equal(12, flop.Pot);
        Assert.Equal((flop.Dealer + 1) % 3, flop.Current); // despues del flop juega el primero a la izquierda del boton

        for (var i = 0; i < 3; i++)
        {
            state = Do(state, "check");
        }

        Assert.Equal(("turn", 4), (Parse(state).Street, Parse(state).Board.Count));
        for (var i = 0; i < 3; i++)
        {
            state = Do(state, "check");
        }

        Assert.Equal(("river", 5), (Parse(state).Street, Parse(state).Board.Count));
        for (var i = 0; i < 3; i++)
        {
            state = Do(state, "check");
        }

        var done = Parse(state);
        Assert.True(done.Done);
        Assert.False(done.Over); // todos siguen con fichas: la partida continua
        Assert.Equal(300L, done.Players.Sum(p => p.Stack));
        Assert.Null(Game.Outcome(state));
        Assert.True(Game.Turn(state)!.Automatic); // pausa hasta la mano siguiente
    }

    [Fact]
    public void A_raise_makes_everyone_answer_again_and_must_respect_the_minimum()
    {
        var state = StartHand(3);

        Assert.Equal("bad_raise", Assert.Throws<TableRuleException>(() => Do(state, "raise", 6)).Code); // menos que la subida minima (8)
        Assert.Equal("bad_raise", Assert.Throws<TableRuleException>(() => Do(state, "raise", 101)).Code); // mas de lo que tiene
        state = Do(state, "raise", 12); // sube a 12: el minimo para la proxima subida es 12 + 8 = 20
        state = Do(state, "call");
        state = Do(state, "call"); // la ciega grande iguala: ya hay flop

        var flop = Parse(state);
        Assert.Equal("flop", flop.Street);
        Assert.Equal(36, flop.Pot);

        // Con una subida en la ronda, el minimo de la siguiente es el tamaño de la anterior.
        state = Do(state, "raise", 10);
        var view = (PokerView)Game.View(state, Parse(state).Current);
        Assert.Equal(20, view.MinRaiseTo); // 10 + 10
        Assert.Equal(["call", "fold", "raise"], view.Actions);
    }

    [Fact]
    public void Invalid_actions_are_rejected_with_a_reason()
    {
        var state = StartHand(3);
        var current = Parse(state).Current;

        Assert.Equal("cannot_check", Assert.Throws<TableRuleException>(() => Do(state, "check")).Code); // hay que pagar la ciega
        Assert.Equal("not_your_turn", Assert.Throws<TableRuleException>(() => Game.Apply(state, (current + 1) % 3, """{"type":"fold"}""", DateTimeOffset.UnixEpoch)).Code);
        Assert.Equal("bad_action", Assert.Throws<TableRuleException>(() => Do(state, "dance")).Code);
        Assert.Equal("bad_action", Assert.Throws<TableRuleException>(() => Do(state, "raise")).Code); // falta el monto
        Assert.Equal("bad_action", Assert.Throws<TableRuleException>(() => Game.Apply(state, current, "no es json", DateTimeOffset.UnixEpoch)).Code);

        var free = Do(Do(Do(state, "call"), "call"), "check"); // en el flop no hay nada que pagar
        Assert.Equal("nothing_to_call", Assert.Throws<TableRuleException>(() => Do(free, "call")).Code);
    }

    [Fact]
    public void When_everybody_folds_the_last_player_takes_the_pot_without_showing_cards()
    {
        var state = StartHand(3);
        var start = Parse(state);
        state = Do(state, "fold");
        state = Do(state, "fold");

        var done = Parse(state);
        Assert.True(done.Done);
        Assert.False(done.Over);
        Assert.Empty(done.Showdown); // nadie muestra nada
        var stacks = Stacks(state);
        Assert.Equal(300L, stacks.Sum());
        Assert.Equal(102, stacks[start.BigBlindSeat]); // gana las ciegas: 100 - 4 + 6
        Assert.Equal(98, stacks[start.SmallBlindSeat]); // perdio su ciega chica
        Assert.Equal(100, stacks[(start.BigBlindSeat + 1) % 3]); // el que se retiro sin poner nada
        Assert.Equal("hand_over", Assert.Throws<TableRuleException>(() => Do(state, "fold")).Code); // entre manos solo se pasa a la siguiente
    }

    [Fact]
    public void All_in_and_called_runs_out_the_board_and_the_best_hand_takes_everything()
    {
        // Dos jugadores: el que juega primero tiene ases y el otro reyes; el tablero no ayuda a nadie.
        var probe = Parse(StartHand(2));
        var first = probe.Current;
        var hole = new int[2][];
        hole[first] = [C(Spades, 14), C(Hearts, 14)];
        hole[1 - first] = [C(Spades, 13), C(Hearts, 13)];
        var state = Crafted(2, hole, [C(Clubs, 2), C(Diamonds, 7), C(Clubs, 9), C(Diamonds, 3), C(Clubs, 4)]);

        state = Do(state, "raise", 100); // all-in
        state = Do(state, "call");

        var done = Parse(state);
        Assert.True(done.Done);
        Assert.Equal(5, done.Board.Count); // se repartio lo que faltaba
        Assert.Equal(2, done.Showdown.Count);
        Assert.True(done.Over); // el de los reyes se quedo sin fichas: queda uno con todo
        Assert.Equal([200L, 0L], first == 0 ? Game.Outcome(state)!.Payouts : Game.Outcome(state)!.Payouts.Reverse());
        Assert.Null(Game.Turn(state));
        Assert.Equal("finished", Assert.Throws<TableRuleException>(() => Game.Apply(state, done.Current, """{"type":"next"}""", DateTimeOffset.UnixEpoch)).Code);
        Assert.Equal("Pair", done.Showdown.Single(x => x.Won).Category);
        Assert.Equal([C(Spades, 14), C(Hearts, 14)], done.Showdown.Single(x => x.Won).Cards);
    }

    [Fact]
    public void A_tie_splits_the_pot_and_the_odd_chip_goes_to_the_first_winner_left_of_the_dealer()
    {
        // Tres jugadores all-in con 101 de entrada (pozo 303). El tablero es una escalera real de picas: todos juegan el tablero y empatan.
        int[][] hole = [[C(Clubs, 2), C(Diamonds, 3)], [C(Clubs, 4), C(Diamonds, 5)], [C(Clubs, 6), C(Diamonds, 7)]];
        var state = Crafted(3, hole, [C(Spades, 14), C(Spades, 13), C(Spades, 12), C(Spades, 11), C(Spades, 10)], buyIn: 101);

        state = Do(state, "raise", 101); // all-in del primero
        state = Do(state, "call");
        state = Do(state, "call");

        var done = Parse(state);
        Assert.True(done.Done);
        Assert.All(done.Showdown, x => Assert.True(x.Won)); // todos juegan la escalera real del tablero
        var stacks = Stacks(state);
        Assert.Equal(303L, stacks.Sum());
        Assert.Equal(101L, stacks.Min()); // nadie gana ni pierde
        Assert.Equal(101L, stacks.Max());
        Assert.False(done.Over);
    }

    [Fact]
    public void The_pot_is_split_among_tied_winners_and_the_remainder_goes_left_of_the_dealer()
    {
        // Dos de tres tienen la misma mano ganadora; el tercero, mas baja. Pozo 3 x 101 = 303 -> 151 cada uno y 1 de resto.
        var probe = Parse(StartHand(3, buyIn: 101));
        var dealer = probe.Dealer;
        var hole = new int[3][];
        var order = new[] { (dealer + 1) % 3, (dealer + 2) % 3, dealer }; // a la izquierda del boton, en orden
        hole[order[0]] = [C(Hearts, 14), C(Clubs, 13)]; // A-K
        hole[order[1]] = [C(Diamonds, 14), C(Spades, 13)]; // A-K tambien: empata
        hole[order[2]] = [C(Hearts, 3), C(Clubs, 4)]; // peor
        var state = Crafted(3, hole, [C(Spades, 2), C(Hearts, 7), C(Diamonds, 9), C(Clubs, 11), C(Spades, 5)], buyIn: 101);

        state = Do(state, "raise", 101);
        state = Do(state, "call");
        state = Do(state, "call");

        var stacks = Stacks(state);
        Assert.Equal(303L, stacks.Sum());
        Assert.Equal(152L, stacks[order[0]]); // 151 + el resto de 1: el primero a la izquierda del boton
        Assert.Equal(151L, stacks[order[1]]);
        Assert.Equal(0L, stacks[order[2]]);
    }

    [Fact]
    public void A_player_sees_only_their_own_cards_until_showdown()
    {
        var state = StartHand(3);

        var mine = (PokerView)Game.View(state, 1);
        Assert.Equal(2, mine.Hand.Count);
        Assert.Equal(mine.Hand, mine.Players[1].Cards);
        Assert.Null(mine.Players[0].Cards);
        Assert.Null(mine.Players[2].Cards);
        Assert.Equal(2, mine.Players[0].CardCount);
        Assert.Empty(((PokerView)Game.View(state, null)).Hand);

        var json = JsonSerializer.Serialize(mine, Json);
        Assert.DoesNotContain(Parse(state).Seed, json, StringComparison.Ordinal); // ni la semilla de la mesa...
        Assert.Null(mine.HandSeed); // ...ni la de la mano hasta que termine
        Assert.DoesNotContain("deck", json, StringComparison.OrdinalIgnoreCase);

        // Al llegar al showdown las cartas de los que siguen en la mano se muestran a todos.
        var done = StartHand(2);
        done = Do(done, "raise", 100);
        done = Do(done, "call");
        var after = (PokerView)Game.View(done, 0);
        Assert.NotNull(after.Players[0].Cards);
        Assert.NotNull(after.Players[1].Cards);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    public void Bots_playing_against_bots_always_finish_a_match_and_the_chips_are_conserved(int seats)
    {
        for (var match = 0; match < 25; match++)
        {
            var state = Game.Start(new GameStartContext(seats, $"bots-{seats}-{match}", Guid.NewGuid(), 200, DateTimeOffset.UnixEpoch));
            var steps = 0;
            while (Game.Turn(state) is { } turn)
            {
                Assert.True(++steps < 8000, "la partida no termina");
                var action = turn.Automatic ? Game.TimeoutAction(state, turn.Seat) : PokerBot.Choose(Parse(state), turn.Seat);
                state = Game.Apply(state, turn.Seat, action, DateTimeOffset.UnixEpoch);
                var s = Parse(state);
                Assert.All(s.Players, p => Assert.True(p.Stack >= 0));
                // Durante la mano las fichas estan en las pilas y en lo apostado; al terminar, los pozos ya se sumaron a las pilas.
                Assert.Equal(200L * seats, s.Players.Sum(p => p.Stack) + (s.Done ? 0 : s.Pot));
            }

            var payouts = Game.Outcome(state)!.Payouts;
            Assert.Equal(200L * seats, payouts.Sum());
            Assert.Equal(seats <= 2 ? 1 : seats <= 4 ? 2 : 3, payouts.Count(p => p > 0)); // el premio se reparte entre los primeros
        }
    }

    [Fact]
    public void Absent_players_never_bet_and_the_hands_keep_coming()
    {
        var state = StartHand(4);
        var hands = 0;
        for (var steps = 0; steps < 600 && hands < 30; steps++)
        {
            var turn = Game.Turn(state);
            if (turn is null)
            {
                break; // las ciegas suben y se llevan las fichas de los ausentes: la partida puede terminar antes
            }

            var action = Game.BotAction(state, turn.Seat); // lo que hace la plataforma por un asiento ausente
            Assert.DoesNotContain("raise", action);
            state = Game.Apply(state, turn.Seat, action, DateTimeOffset.UnixEpoch);
            var s = Parse(state);
            hands = s.HandNo;
            Assert.Equal(400L, s.Players.Sum(p => p.Stack) + (s.Done ? 0 : s.Pot));
        }

        Assert.True(hands >= 10 || Parse(state).Over, "las manos siguen una tras otra");
        Assert.Equal("""{"type":"fold"}""", Game.TimeoutAction(StartHand(3), Parse(StartHand(3)).Current)); // con algo por pagar, se retira
    }

    // ---- Varias manos ----

    /// <summary>Tres jugadores van all-in con 101; el tercero pierde todo (queda afuera) y la partida sigue de a dos.</summary>
    private static string ThreeWayHandWithOneElimination(long buyIn = 101, int rebuys = 0)
    {
        var probe = Parse(StartHand(3, buyIn: buyIn));
        var dealer = probe.Dealer;
        var hole = new int[3][];
        var order = new[] { (dealer + 1) % 3, (dealer + 2) % 3, dealer };
        hole[order[0]] = [C(Hearts, 14), C(Clubs, 13)];
        hole[order[1]] = [C(Diamonds, 14), C(Spades, 13)];
        hole[order[2]] = [C(Hearts, 3), C(Clubs, 4)];
        var crafted = Parse(Crafted(3, hole, [C(Spades, 2), C(Hearts, 7), C(Diamonds, 9), C(Clubs, 11), C(Spades, 5)], buyIn: buyIn));
        crafted.Rebuys = rebuys;
        return ShoveUntilDone(Serialize(crafted));
    }

    [Fact]
    public void A_player_without_chips_is_out_and_the_next_hand_is_played_without_them()
    {
        var done = ThreeWayHandWithOneElimination();
        var s = Parse(done);
        var outSeat = Array.FindIndex([.. s.Players], p => p.Stack == 0);
        Assert.False(s.Over);
        Assert.Contains(s.Events, e => e.Kind == "out" && e.Seat == outSeat);

        var turn = Game.Turn(done)!;
        Assert.True(turn.Automatic);
        Assert.Equal("""{"type":"next"}""", Game.TimeoutAction(done, turn.Seat));
        var next = Parse(Game.Apply(done, turn.Seat, Game.TimeoutAction(done, turn.Seat), DateTimeOffset.UnixEpoch));

        Assert.Equal(2, next.HandNo);
        Assert.False(next.Done);
        Assert.True(next.Players[outSeat].Out);
        Assert.Empty(next.Players[outSeat].Cards); // el eliminado no recibe cartas ni pone ciegas
        Assert.DoesNotContain(outSeat, new[] { next.Dealer, next.SmallBlindSeat, next.BigBlindSeat, next.Current });
        Assert.Equal(next.Dealer, next.SmallBlindSeat); // de a dos, el boton pone la ciega chica
        Assert.Equal(4, next.Players.Where(p => !p.Out).SelectMany(p => p.Cards).Distinct().Count());
        Assert.Equal(303L, next.Players.Sum(p => p.Stack + p.Total));
    }

    [Fact]
    public void The_dealer_button_moves_to_the_next_player_with_chips_each_hand()
    {
        var state = StartHand(4);
        var dealer = Parse(state).Dealer;
        state = Do(Do(Do(state, "fold"), "fold"), "fold");
        var turn = Game.Turn(state)!;
        state = Game.Apply(state, turn.Seat, Game.TimeoutAction(state, turn.Seat), DateTimeOffset.UnixEpoch);

        Assert.Equal((dealer + 1) % 4, Parse(state).Dealer);
        Assert.Equal(2, Parse(state).HandNo);
    }

    [Fact]
    public void Between_hands_only_the_next_hand_is_accepted_and_during_a_hand_it_is_not()
    {
        var state = StartHand(3);
        Assert.Equal("hand_running", Assert.Throws<TableRuleException>(() => Do(state, "next")).Code);

        var done = Do(Do(state, "fold"), "fold");
        var current = Parse(done).Current;
        Assert.Equal("hand_over", Assert.Throws<TableRuleException>(() => Game.Apply(done, current, """{"type":"raise","to":10}""", DateTimeOffset.UnixEpoch)).Code);
        Assert.Equal("not_your_turn", Assert.Throws<TableRuleException>(() => Game.Apply(done, (current + 1) % 3, """{"type":"next"}""", DateTimeOffset.UnixEpoch)).Code);
    }

    [Fact]
    public void Each_hand_has_its_own_seed_with_a_commitment_published_before_dealing_and_revealed_at_the_end()
    {
        var first = StartHand(3);
        var view1 = (PokerView)Game.View(first, 0);
        Assert.Null(view1.HandSeed); // todavia no se revela
        Assert.Equal(1, view1.HandNo);

        var done = Do(Do(first, "fold"), "fold");
        var revealed = (PokerView)Game.View(done, 0);
        Assert.NotNull(revealed.HandSeed);
        Assert.True(ProvablyFair.MatchesCommitment(revealed.HandSeed!, view1.Commitment)); // la semilla revelada es la que se comprometio
        Assert.Equal(view1.Commitment, revealed.Commitment);

        var turn = Game.Turn(done)!;
        var second = Game.Apply(done, turn.Seat, Game.TimeoutAction(done, turn.Seat), DateTimeOffset.UnixEpoch);
        var view2 = (PokerView)Game.View(second, 0);
        Assert.Equal(2, view2.HandNo);
        Assert.NotEqual(view1.Commitment, view2.Commitment); // otra mano, otra semilla
        Assert.NotEqual(Parse(first).Players[0].Cards, Parse(second).Players[0].Cards);

        // La semilla de cada mano sale de la de la mesa: con la de la mesa revelada se recalcula todo.
        var table = Parse(first);
        Assert.Equal(revealed.HandSeed, PokerGame.HandSeed(table.Seed, table.TableId, 1));
        Assert.NotEqual(PokerGame.HandSeed(table.Seed, table.TableId, 1), PokerGame.HandSeed(table.Seed, table.TableId, 2));
    }

    [Fact]
    public void A_hand_where_the_blinds_leave_nobody_able_to_decide_runs_out_to_the_showdown()
    {
        // Mano de dos con 1 y 2 fichas: las dos ciegas dejan a ambos all-in y las cartas salen solas.
        var done = Do(Do(StartHand(3), "fold"), "fold");
        var s = Parse(done);
        s.Players[0].Stack = 0;
        s.Players[1].Stack = 1;
        s.Players[2].Stack = 2;
        var turn = Game.Turn(Serialize(s))!;

        var next = Parse(Game.Apply(Serialize(s), turn.Seat, Game.TimeoutAction(Serialize(s), turn.Seat), DateTimeOffset.UnixEpoch));

        Assert.True(next.Done);
        Assert.Equal(5, next.Board.Count);
        Assert.Equal(3L, next.Players.Sum(p => p.Stack));
    }

    // ---- Pozos laterales ----

    [Fact]
    public void An_all_in_for_less_builds_a_side_pot_and_each_pot_goes_to_the_best_hand_among_those_who_reached_it()
    {
        // Tres jugadores con 30, 80 y 200. El corto tiene la mejor mano, el mediano la segunda y el largo la peor; el largo abre y los otros igualan all-in.
        int[][] hole =
        [
            [C(Spades, 14), C(Hearts, 14)], // el corto: ases
            [C(Spades, 13), C(Hearts, 13)], // el mediano: reyes
            [C(Spades, 12), C(Hearts, 12)], // el largo: damas
        ];
        var state = Crafted(3, hole, [C(Clubs, 2), C(Diamonds, 7), C(Clubs, 9), C(Diamonds, 3), C(Clubs, 4)]);
        state = WithTurn(WithStartingStacks(state, 30, 80, 200), 2);

        var done = Parse(ShoveUntilDone(state));

        Assert.Equal([90L, 100L, 120L], done.Pots.Select(p => p.Amount)); // principal 3 x 30, lateral 2 x 50 y lo que nadie le igualo al largo
        Assert.Equal([[0, 1, 2], [1, 2], [2]], done.Pots.Select(p => p.Eligible));
        Assert.Equal([[0], [1], [2]], done.Pots.Select(p => p.Winners));
        Assert.Equal([90L, 100L, 120L], done.Players.Select(p => p.Stack));
        Assert.False(done.Over);
    }

    [Fact]
    public void The_short_stack_with_the_best_hand_wins_only_the_main_pot()
    {
        int[][] hole =
        [
            [C(Spades, 14), C(Hearts, 14)], // el corto: ases
            [C(Spades, 12), C(Hearts, 12)], // el mediano: damas
            [C(Spades, 13), C(Hearts, 13)], // el largo: reyes
        ];
        var state = Crafted(3, hole, [C(Clubs, 2), C(Diamonds, 7), C(Clubs, 9), C(Diamonds, 3), C(Clubs, 4)]);
        state = WithTurn(WithStartingStacks(state, 30, 80, 200), 2);

        var done = Parse(ShoveUntilDone(state));

        Assert.Equal([90L, 0L, 220L], done.Players.Select(p => p.Stack)); // los ases cobran el principal; el lateral y lo que nadie igualo son del largo
        Assert.False(done.Over);
        Assert.True(done.Players[1].Stack == 0 && done.Players[1].Out); // el mediano se quedo sin fichas y no tiene recompras: afuera
    }

    [Fact]
    public void The_worst_hand_for_the_big_stack_leaves_only_the_big_stack_with_chips()
    {
        int[][] hole =
        [
            [C(Hearts, 3), C(Clubs, 4)],
            [C(Spades, 13), C(Hearts, 13)],
            [C(Spades, 14), C(Hearts, 14)],
        ];
        var state = Crafted(3, hole, [C(Clubs, 2), C(Diamonds, 7), C(Clubs, 9), C(Diamonds, 11), C(Clubs, 5)]);
        state = WithTurn(WithStartingStacks(state, 30, 80, 200), 2);

        var done = Parse(ShoveUntilDone(state));

        Assert.Equal([0L, 0L, 310L], done.Players.Select(p => p.Stack));
        Assert.True(done.Over);
        // El largo es primero; el mediano (arrancaba con 80) se fue despues del corto (30): segundo. Premio 65 % / 35 % de 3 x 100.
        Assert.Equal([2, 1, 0], PokerGame.Ranking(done));
        Assert.Equal([0L, 105L, 195L], Game.Outcome(Serialize(done))!.Payouts);
    }

    [Fact]
    public void The_pots_add_up_to_everything_that_was_put_in_even_when_a_player_folds()
    {
        var state = StartHand(3);
        state = Do(state, "raise", 60);
        state = Do(state, "fold");
        state = Do(state, "call");
        Assert.Equal(300L, Parse(state).Players.Sum(p => p.Stack) + Parse(state).Pot);

        while (!Parse(state).Done)
        {
            state = Do(state, "check"); // los dos que quedan pasan hasta el showdown
        }

        var done = Parse(state);
        Assert.Equal(300L, done.Players.Sum(p => p.Stack));
        Assert.Equal(done.Pot, done.Pots.Sum(p => p.Amount));
    }

    [Fact]
    public void Pots_are_built_by_level_counting_what_folded_players_put_in()
    {
        var state = new PokerState { Seats = 4, Players = [new PokerPlayer { Total = 10, Folded = true }, new PokerPlayer { Total = 30 }, new PokerPlayer { Total = 50 }, new PokerPlayer { Total = 50 }] };

        var pots = PokerGame.BuildPots(state);

        Assert.Equal([100L, 40L], pots.Select(p => p.Amount)); // 10 + 30 + 30 + 30 y 20 + 20
        Assert.Equal([[1, 2, 3], [2, 3]], pots.Select(p => p.Eligible));
        Assert.Equal(140L, pots.Sum(p => p.Amount));
    }

    [Fact]
    public void What_a_folded_player_put_in_above_the_top_level_goes_to_the_last_pot()
    {
        var state = new PokerState { Seats = 3, Players = [new PokerPlayer { Total = 60, Folded = true }, new PokerPlayer { Total = 30 }, new PokerPlayer { Total = 40 }] };

        var pots = PokerGame.BuildPots(state);

        Assert.Equal([90L, 40L], pots.Select(p => p.Amount));
        Assert.Equal(130L, pots.Sum(p => p.Amount)); // todo lo que se puso (60 + 30 + 40) queda en algun pozo
    }

    [Fact]
    public void Nobody_can_raise_when_everyone_else_is_all_in()
    {
        // El primero va all-in por poco; el segundo (con muchas fichas) solo puede igualar o retirarse, no subir.
        var state = StartHand(2);
        var first = Parse(state).Current;
        var s = Parse(state);
        s.Players[first].Stack = 6; // le quedan 6 fichas (all-in corto)
        state = Serialize(s);

        state = Do(state, "raise", Parse(state).Players[first].Bet + 6); // all-in
        var view = (PokerView)Game.View(state, Parse(state).Current);

        Assert.DoesNotContain("raise", view.Actions);
        Assert.Equal("cannot_raise", Assert.Throws<TableRuleException>(() => Do(state, "raise", 50)).Code);
    }

    [Fact]
    public void Poker_has_no_bots()
    {
        Assert.False(Game.AllowsBots);
    }

    // ---- Torneo: opciones, ciegas crecientes, recompras, premios y comision ----

    private static JsonElement Opts(string json) => JsonDocument.Parse(json).RootElement;

    private static string StartTournament(int seats, long buyIn, string options, string seed = "poker-test-seed")
    {
        var quote = Game.Quote(buyIn, seats, Opts(options));
        return Game.Start(new GameStartContext(seats, seed, TableId, buyIn, DateTimeOffset.UnixEpoch, quote.OptionsJson));
    }

    [Fact]
    public void Quoting_a_table_computes_what_is_reserved_and_validates_the_options()
    {
        var plain = Game.Quote(100, 3, null);
        Assert.Equal(110, plain.Stake); // la entrada mas el 10 % de comision
        Assert.Contains("\"bigBlind\":4", plain.OptionsJson);
        Assert.Contains("sin recompras", plain.Description);

        var withRebuys = Game.Quote(100, 3, Opts("""{"bigBlind":4,"rebuys":2}"""));
        Assert.Equal(330, withRebuys.Stake); // tres entradas posibles de 100 + 10
        Assert.Contains("2 recompras", withRebuys.Description);
        Assert.Contains("330", withRebuys.Breakdown);

        Assert.Equal("bad_blind", Assert.Throws<TableRuleException>(() => Game.Quote(100, 3, Opts("""{"bigBlind":3}"""))).Code); // impar
        Assert.Equal("bad_blind", Assert.Throws<TableRuleException>(() => Game.Quote(100, 3, Opts("""{"bigBlind":1}"""))).Code);
        Assert.Equal("bad_blind", Assert.Throws<TableRuleException>(() => Game.Quote(100, 3, Opts("""{"bigBlind":10}"""))).Code); // 10 ciegas grandes: menos de 20
        Assert.Equal("bad_blind", Assert.Throws<TableRuleException>(() => Game.Quote(1_000, 3, Opts("""{"bigBlind":8}"""))).Code); // 125 ciegas grandes: mas de 100
        Assert.Equal("bad_rebuys", Assert.Throws<TableRuleException>(() => Game.Quote(100, 3, Opts("""{"rebuys":3}"""))).Code);
        Assert.Equal("bad_option", Assert.Throws<TableRuleException>(() => Game.Quote(100, 3, Opts("""{"bigBlind":"mucha"}"""))).Code);
        Assert.Equal(3, Game.Options.Count); // modalidad, ciega grande y recompras
    }

    [Fact]
    public void The_blinds_rise_every_ten_hands_rounded_up_to_an_even_number()
    {
        Assert.Equal([4L, 4L, 6L, 6L, 10L, 16L, 24L], HandsChecked.Select(h => PokerGame.BigBlindAt(4, h, 10, 50)));

        var state = StartTournament(3, 100, """{"bigBlind":4}""");
        var level = (PokerView)Game.View(state, 0);
        Assert.Equal((2L, 4L, 6L, 10), (level.SmallBlind, level.BigBlind, level.NextBigBlind, level.HandsToNextLevel));

        // Jugando manos de seguido (nadie apuesta) las ciegas suben en la mano 11 y las pilas siguen sumando lo mismo.
        for (var guard = 0; Parse(state).HandNo < 11 && guard < 500; guard++)
        {
            var turn = Game.Turn(state)!;
            state = Game.Apply(state, turn.Seat, Game.TimeoutAction(state, turn.Seat), DateTimeOffset.UnixEpoch);
        }

        var tenth = Parse(state);
        Assert.Equal(11, tenth.HandNo);
        Assert.Equal((3L, 6L), (tenth.SmallBlind, tenth.BigBlind));
        Assert.Equal(300L, tenth.Players.Sum(p => p.Stack + p.Total));
    }

    /// <summary>Tres jugadores con una recompra por cabeza: uno se queda sin fichas en la primera mano y queda esperando decidir.</summary>
    private static string HandWithAPlayerWaitingToRebuy(int rebuys = 1)
    {
        var state = Parse(ThreeWayHandWithOneElimination(buyIn: 100, rebuys: rebuys));
        return Serialize(state);
    }

    [Fact]
    public void A_player_who_runs_out_of_chips_with_rebuys_left_waits_to_decide_instead_of_being_out()
    {
        var done = HandWithAPlayerWaitingToRebuy();
        var s = Parse(done);
        var waiting = Array.FindIndex([.. s.Players], p => p.AwaitingRebuy);

        Assert.True(waiting >= 0);
        Assert.False(s.Players[waiting].Out);
        Assert.False(s.Over); // todavia puede volver
        Assert.Equal(20d, Game.Turn(done)!.Seconds); // ventana larga para decidir
        Assert.True(Game.MayAct(done, waiting)); // puede decidir aunque no sea su turno
        var view = (PokerView)Game.View(done, waiting);
        Assert.Equal(["decline", "rebuy"], view.Actions);
        Assert.Equal(1, view.Players[waiting].RebuysLeft);
        Assert.Empty(((PokerView)Game.View(done, (waiting + 1) % 3)).Actions);
    }

    [Fact]
    public void Rebuying_gives_a_new_entry_of_chips_shortens_the_pause_and_the_hand_goes_on_with_them()
    {
        var done = HandWithAPlayerWaitingToRebuy();
        var waiting = Array.FindIndex([.. Parse(done).Players], p => p.AwaitingRebuy);

        var after = Parse(Game.Apply(done, waiting, """{"type":"rebuy"}""", DateTimeOffset.UnixEpoch));

        Assert.Equal(100L, after.Players[waiting].Stack);
        Assert.Equal(1, after.Players[waiting].RebuysUsed);
        Assert.False(after.Players[waiting].AwaitingRebuy);
        Assert.Equal(1, after.RebuyCount);
        Assert.Equal(400L, after.PrizePool); // la recompra suma una entrada al premio (sin la comision)
        Assert.Equal(3d, Game.Turn(Serialize(after))!.Seconds); // ya decidieron todos: la pausa se acorta
        Assert.Contains(after.Events, e => e.Kind == "rebuy" && e.Seat == waiting);
        Assert.Equal("cannot_rebuy", Assert.Throws<TableRuleException>(() => Game.Apply(Serialize(after), waiting, """{"type":"rebuy"}""", DateTimeOffset.UnixEpoch)).Code);

        var turn = Game.Turn(Serialize(after))!;
        var next = Parse(Game.Apply(Serialize(after), turn.Seat, Game.TimeoutAction(Serialize(after), turn.Seat), DateTimeOffset.UnixEpoch));
        Assert.Equal(2, next.HandNo);
        Assert.Equal(2, next.Players[waiting].Cards.Count); // vuelve a recibir cartas
        Assert.False(next.Players[waiting].Out);
    }

    [Fact]
    public void Declining_the_rebuy_eliminates_the_player_and_the_last_one_with_chips_ends_the_match_at_once()
    {
        // Dos jugadores, una recompra cada uno: el que pierde el all-in decide no recomprar.
        var probe = Parse(StartHand(2));
        var hole = new int[2][];
        hole[probe.Current] = [C(Spades, 14), C(Hearts, 14)];
        hole[1 - probe.Current] = [C(Spades, 13), C(Hearts, 13)];
        var state = Parse(Crafted(2, hole, [C(Clubs, 2), C(Diamonds, 7), C(Clubs, 9), C(Diamonds, 3), C(Clubs, 4)]));
        state.Rebuys = 1;
        var done = Parse(ShoveUntilDone(Serialize(state)));
        var loser = Array.FindIndex([.. done.Players], p => p.Stack == 0);
        Assert.True(done.Players[loser].AwaitingRebuy);
        Assert.False(done.Over);

        var after = Parse(Game.Apply(Serialize(done), loser, """{"type":"decline"}""", DateTimeOffset.UnixEpoch));

        Assert.True(after.Over); // nadie mas puede recomprar y queda uno solo con fichas
        Assert.Equal([1 - loser, loser], PokerGame.Ranking(after));
        // El premio (2 x 100) es todo del primero; el que no recompro recupera la recompra que habia reservado (100).
        Assert.Equal([300L, 100L], (1 - loser) == 0 ? Game.Outcome(Serialize(after))!.Payouts : Game.Outcome(Serialize(after))!.Payouts.Reverse());
    }

    [Fact]
    public void Letting_the_pause_run_out_without_deciding_eliminates_the_player()
    {
        var done = HandWithAPlayerWaitingToRebuy();
        var s = Parse(done);
        var waiting = Array.FindIndex([.. s.Players], p => p.AwaitingRebuy);
        var turn = Game.Turn(done)!;

        var next = Parse(Game.Apply(done, turn.Seat, Game.TimeoutAction(done, turn.Seat), DateTimeOffset.UnixEpoch)); // vence la pausa

        Assert.True(next.Players[waiting].Out);
        Assert.False(next.Players[waiting].AwaitingRebuy);
        Assert.Equal([waiting], next.Eliminated);
        Assert.Equal(2, next.HandNo);
        Assert.Empty(next.Players[waiting].Cards);
    }

    [Fact]
    public void Once_all_the_rebuys_are_used_a_player_without_chips_is_out_for_good()
    {
        var done = Parse(HandWithAPlayerWaitingToRebuy(rebuys: 1));
        var waiting = Array.FindIndex([.. done.Players], p => p.AwaitingRebuy);
        done.Players[waiting].RebuysUsed = 1; // ya uso la unica que tenia
        done.Players[waiting].AwaitingRebuy = false;
        done.Players[waiting].Out = true;

        Assert.Equal("cannot_rebuy", Assert.Throws<TableRuleException>(() => Game.Apply(Serialize(done), waiting, """{"type":"rebuy"}""", DateTimeOffset.UnixEpoch)).Code);
    }

    [Fact]
    public void Rebuying_is_not_possible_while_a_hand_is_running()
    {
        var state = StartHand(3);
        Assert.Equal("hand_running", Assert.Throws<TableRuleException>(() => Do(state, "rebuy")).Code);
        Assert.Equal("hand_running", Assert.Throws<TableRuleException>(() => Do(state, "decline")).Code);
    }

    [Fact]
    public void The_prize_is_shared_among_the_first_places_depending_on_how_many_play()
    {
        Assert.Equal([300L], PokerGame.Prizes(300, 2));
        Assert.Equal([195L, 105L], PokerGame.Prizes(300, 3));
        Assert.Equal([260L, 140L], PokerGame.Prizes(400, 4));
        Assert.Equal([150L, 90L, 60L], PokerGame.Prizes(300, 5));
        Assert.Equal([300L, 180L, 120L], PokerGame.Prizes(600, 6));
        Assert.Equal([66L, 35L], PokerGame.Prizes(101, 3)); // el resto del redondeo va al primero
        Assert.All(SeatCounts, seats => Assert.Equal(777L, PokerGame.Prizes(777, seats).Sum()));
    }

    [Fact]
    public void The_payouts_return_unused_rebuys_and_the_house_keeps_the_fees_so_everything_adds_up()
    {
        // Tres jugadores con entrada 100, comision 10 y hasta 2 recompras: se reservaron 3 x 330. El primero uso una recompra.
        var state = new PokerState
        {
            Seats = 3, BuyIn = 100, Fee = 10, Rebuys = 2, RebuyCount = 1, Over = true, Done = true, Eliminated = [1, 2],
            Players = [new PokerPlayer { Stack = 400, RebuysUsed = 1 }, new PokerPlayer { Out = true, Ranked = true }, new PokerPlayer { Out = true, Ranked = true }],
        };

        var outcome = Game.Outcome(Serialize(state))!;

        // Premio: 4 entradas = 400 -> 260 y 140. Reintegros: lo que cada uno reservo y no uso (110 por recompra sin usar).
        Assert.Equal([370L, 220L, 360L], outcome.Payouts); // 260 + 110 | 0 + 220 | 140 + 220
        Assert.Equal(40L, outcome.HouseTake); // 4 entradas pagadas x 10
        Assert.Equal(3 * 330L, outcome.Payouts.Sum() + outcome.HouseTake);
    }

    [Fact]
    public void A_tournament_view_shows_the_prize_pool_the_prizes_and_the_final_ranking()
    {
        var state = StartTournament(3, 100, """{"bigBlind":4,"rebuys":1}""");
        var view = (PokerView)Game.View(state, 0);
        Assert.Equal((1, 100L, 10L, 300L), (view.Rebuys, view.Entry, view.Fee, view.PrizePool));
        Assert.Equal([195L, 105L], view.Prizes);
        Assert.Empty(view.Ranking); // todavia se esta jugando
        Assert.Equal(1, view.Players[0].RebuysLeft);
    }

    [Fact]
    public void A_full_tournament_with_rebuys_between_bots_always_closes_the_accounts()
    {
        for (var match = 0; match < 15; match++)
        {
            var seats = 2 + (match % 5);
            var state = StartTournament(seats, 200, """{"bigBlind":4,"rebuys":2}""", seed: $"torneo-{match}");
            var steps = 0;
            while (Game.Turn(state) is { } turn)
            {
                Assert.True(++steps < 20_000, "el torneo no termina");
                var s = Parse(state);
                string action;
                if (s.Done)
                {
                    // Los que se quedaron sin fichas recompran si pueden (asi se prueba el camino completo); despues pasa la pausa.
                    var waiting = Array.FindIndex([.. s.Players], p => p.AwaitingRebuy);
                    action = waiting >= 0 ? $$"""{"type":"rebuy"}""" : Game.TimeoutAction(state, turn.Seat);
                    state = Game.Apply(state, waiting >= 0 ? waiting : turn.Seat, action, DateTimeOffset.UnixEpoch);
                }
                else
                {
                    state = Game.Apply(state, turn.Seat, PokerBot.Choose(s, turn.Seat), DateTimeOffset.UnixEpoch);
                }
            }

            var outcome = Game.Outcome(state)!;
            var final = Parse(state);
            var reservedEach = (200L + 20L) * 3; // entrada + comision, x (1 + 2 recompras)
            Assert.Equal(reservedEach * seats, outcome.Payouts.Sum() + outcome.HouseTake);
            Assert.Equal((seats + final.RebuyCount) * 20L, outcome.HouseTake);
            Assert.All(outcome.Payouts, p => Assert.True(p >= 0));
        }
    }

    // ---- Mesa libre (cash): asientos que se ocupan y se liberan, recarga, rake y espera ----

    private static string CashOptions(long bigBlind = 4) => $$"""{"mode":1,"bigBlind":{{bigBlind}}}""";

    private static string StartCash(int seats = 6, long creatorEntry = 100, long bigBlind = 4)
    {
        var quote = Game.Quote(creatorEntry, seats, Opts(CashOptions(bigBlind)));
        return Game.Start(new GameStartContext(seats, "cash-seed", TableId, creatorEntry, DateTimeOffset.UnixEpoch, quote.OptionsJson));
    }

    private static string Sit(string state, int seat, long chips) => Game.SeatPlayer(state, seat, chips, DateTimeOffset.UnixEpoch);

    private static string Top(string state, int seat, long chips) => Game.AddChips(state, seat, chips, DateTimeOffset.UnixEpoch);

    private static string Leave(string state, int seat) => Game.Apply(state, seat, """{"type":"leave"}""", DateTimeOffset.UnixEpoch);

    private static string TwoSeated(long first = 100, long second = 100, long bigBlind = 4) => Sit(Sit(StartCash(bigBlind: bigBlind), 0, first), 1, second);

    /// <summary>Pasa de una mano a la siguiente: juega el turno automatico de la pausa.</summary>
    private static string NextHand(string state)
    {
        var turn = Game.Turn(state)!;
        Assert.True(turn.Automatic);
        return Game.Apply(state, turn.Seat, Game.TimeoutAction(state, turn.Seat), DateTimeOffset.UnixEpoch);
    }

    /// <summary>Le pone las cartas a la mano en curso: las de cada asiento (por numero de asiento) y las cinco comunitarias.</summary>
    private static string WithCards(string state, Dictionary<int, int[]> hole, int[] board)
    {
        var s = Parse(state);
        foreach (var (seat, cards) in hole)
        {
            s.Players[seat].Cards = [.. cards];
        }

        s.Deck = [.. board, .. s.Deck.Where(c => !board.Contains(c) && !hole.Values.SelectMany(h => h).Contains(c))];
        return Serialize(s);
    }

    private static long Chips(PokerState s) => s.Players.Sum(p => p.Stack + p.PendingChips) + s.CashOuts.Sum(c => c.Chips);

    [Fact]
    public void A_cash_table_quote_reserves_the_entry_and_reports_the_range_and_the_rake()
    {
        var quote = Game.Quote(100, 6, Opts(CashOptions()));

        Assert.True(quote.Continuous);
        Assert.Equal(100, quote.Stake); // sin comision de inscripcion: la casa cobra rake de los pozos
        Assert.Equal((80L, 400L), (quote.MinEntry, quote.MaxEntry)); // de 20 a 100 ciegas grandes
        Assert.Contains("Mesa libre", quote.Description);
        Assert.Contains("rake 3 %", quote.Description);
        Assert.Contains("\"cash\":true", quote.OptionsJson);
        Assert.Equal("bad_blind", Assert.Throws<TableRuleException>(() => Game.Quote(50, 6, Opts(CashOptions()))).Code); // 50 no llega a 20 ciegas grandes
        Assert.Equal("bad_rebuys", Assert.Throws<TableRuleException>(() => Game.Quote(100, 6, Opts("""{"mode":1,"bigBlind":4,"rebuys":1}"""))).Code);
        Assert.Equal("bad_option", Assert.Throws<TableRuleException>(() => Game.Quote(100, 6, Opts("""{"mode":5}"""))).Code);
        Assert.False(Game.Quote(100, 3, Opts("""{"mode":0,"bigBlind":4}""")).Continuous); // el torneo sigue igual
    }

    [Fact]
    public void A_cash_table_starts_empty_and_waiting_until_two_players_sit()
    {
        var state = StartCash();
        var s = Parse(state);

        Assert.True(s.Cash);
        Assert.True(s.Waiting);
        Assert.All(s.Players, p => Assert.True(p.Vacant));
        Assert.Null(Game.Turn(state)); // no hay nada que jugar
        Assert.Null(Game.Outcome(state)); // y nunca termina por si sola

        var one = Sit(state, 2, 100);
        Assert.True(Parse(one).Waiting); // uno solo no alcanza
        Assert.Null(Game.Turn(one));

        var two = Sit(one, 4, 150);
        var started = Parse(two);
        Assert.False(started.Waiting);
        Assert.Equal(1, started.HandNo);
        Assert.NotNull(Game.Turn(two));
        Assert.Equal(2, started.Players.Count(p => p.Cards.Count == 2));
        Assert.Equal(250L, started.Players.Sum(p => p.Stack + p.Total));
    }

    [Fact]
    public void Seating_checks_the_seat_and_the_entry_range()
    {
        var state = StartCash();

        Assert.Equal("bad_entry", Assert.Throws<TableRuleException>(() => Sit(state, 0, 79)).Code);
        Assert.Equal("bad_entry", Assert.Throws<TableRuleException>(() => Sit(state, 0, 401)).Code);
        Assert.Equal("seat_taken", Assert.Throws<TableRuleException>(() => Sit(state, 9, 100)).Code);
        var seated = Sit(state, 0, 100);
        Assert.Equal("seat_taken", Assert.Throws<TableRuleException>(() => Sit(seated, 0, 100)).Code);
        Assert.Equal("not_cash", Assert.Throws<TableRuleException>(() => Sit(StartHand(3), 0, 100)).Code);
    }

    [Fact]
    public void Someone_who_sits_during_a_hand_waits_for_the_next_one_without_touching_the_pots()
    {
        var state = TwoSeated();
        var before = Parse(state);

        var three = Parse(Sit(state, 2, 200));

        Assert.True(three.Players[2].Folded); // mira esta mano
        Assert.Empty(three.Players[2].Cards);
        Assert.Equal(before.Pot, three.Pot);

        var done = Do(Serialize(three), "fold");
        var next = Parse(NextHand(done));
        Assert.Equal(2, next.HandNo);
        Assert.Equal(3, next.Players.Count(p => p.Cards.Count == 2)); // ahora si juega
    }

    [Fact]
    public void The_rake_is_a_percentage_of_every_pot_that_sees_the_flop_and_the_chips_always_add_up()
    {
        var state = TwoSeated();
        var first = Parse(state).Current;
        state = WithCards(state, new Dictionary<int, int[]> { [first] = [C(Spades, 14), C(Hearts, 14)], [1 - first] = [C(Spades, 13), C(Hearts, 13)] }, [C(Clubs, 2), C(Diamonds, 7), C(Clubs, 9), C(Diamonds, 3), C(Clubs, 4)]);

        var done = Parse(ShoveUntilDone(state));

        Assert.Equal(200, done.Pots.Sum(p => p.Amount));
        Assert.Equal(6, done.Pots.Single().Rake); // 3 % de 200
        Assert.Equal((6L, 6L), (done.HandRake, done.RakeTotal));
        Assert.Equal(194L, done.Players[first].Stack); // el ganador cobra el pozo menos el rake
        Assert.Equal(200L, Chips(done) + done.RakeTotal); // nada se pierde ni se crea
        Assert.Null(Game.Outcome(Serialize(done)));
    }

    [Fact]
    public void No_flop_no_drop_and_small_pots_round_down_to_no_rake()
    {
        // Preflop todos se retiran: el pozo es de las ciegas y la mano no llego al flop.
        var folded = Parse(Do(TwoSeated(), "fold"));
        Assert.Equal(0, folded.RakeTotal);
        Assert.Equal(200L, folded.Players.Sum(p => p.Stack));

        // Llega al flop con un pozo chico (3 % de 8 = 0,24): se redondea hacia abajo y no se cobra nada.
        var state = TwoSeated(bigBlind: 2);
        state = Do(state, "call"); // el que tiene el boton iguala
        state = Do(state, "check"); // la ciega grande pasa: flop con 4 en el pozo
        state = Do(state, "check");
        state = Do(state, "fold"); // se retira en el flop: gana el que quedo, con el rake redondeado a cero
        var done = Parse(state);
        Assert.Equal(3, done.Board.Count);
        Assert.Equal(0, done.RakeTotal);
        Assert.Equal(200L, done.Players.Sum(p => p.Stack));
    }

    [Fact]
    public void The_rake_is_taken_from_each_side_pot_but_not_from_a_bet_nobody_called()
    {
        // Entradas de 40, 100 y 200 (ciega grande 2: de 40 a 200). El corto tiene la mejor mano, el mediano la segunda y el largo la peor.
        var state = Sit(Sit(Sit(StartCash(bigBlind: 2, creatorEntry: 100), 0, 40), 1, 100), 2, 200);
        state = NextHand(Do(state, "fold")); // la primera mano es de dos (el tercero espera): se juega rapido y la segunda ya es de tres
        state = Serialize(WithStacks(Parse(state), 40, 100, 200));
        var hole = new Dictionary<int, int[]>
        {
            [0] = [C(Spades, 14), C(Hearts, 14)],
            [1] = [C(Spades, 13), C(Hearts, 13)],
            [2] = [C(Spades, 12), C(Hearts, 12)],
        };
        state = WithTurn(WithCards(state, hole, [C(Clubs, 2), C(Diamonds, 7), C(Clubs, 9), C(Diamonds, 3), C(Clubs, 4)]), 2);

        var done = Parse(ShoveUntilDone(state));

        Assert.Equal([120L, 120L, 100L], done.Pots.Select(p => p.Amount)); // principal 3 x 40, lateral 2 x 60 y lo que nadie le igualo al largo
        Assert.Equal([3L, 3L, 0L], done.Pots.Select(p => p.Rake)); // 3 % de cada pozo (redondeado hacia abajo); lo no igualado no paga
        Assert.Equal([117L, 117L, 100L], done.Players.Take(3).Select(p => p.Stack));
        Assert.Equal(340L, Chips(done) + done.RakeTotal);
    }

    /// <summary>Pone las pilas con las que arranco cada asiento la mano (antes de las ciegas), para armar pilas desiguales.</summary>
    private static PokerState WithStacks(PokerState s, params long[] starting)
    {
        for (var i = 0; i < starting.Length; i++)
        {
            s.Players[i].Stack = starting[i] - s.Players[i].Total;
        }

        return s;
    }

    [Fact]
    public void A_top_up_enters_the_stack_at_the_next_hand_and_cannot_exceed_the_maximum()
    {
        var state = TwoSeated();

        var topped = Parse(Top(state, 0, 150));
        Assert.Equal(150L, topped.Players[0].PendingChips);
        Assert.Equal(100L, topped.Players[0].Stack + topped.Players[0].Total); // la mano en curso sigue con la pila de antes
        Assert.Equal("too_much", Assert.Throws<TableRuleException>(() => Top(Serialize(topped), 0, 200)).Code); // 100 + 150 + 200 > 400
        Assert.Equal("not_seated", Assert.Throws<TableRuleException>(() => Top(state, 5, 50)).Code);
        Assert.Equal("not_seated", Assert.Throws<TableRuleException>(() => Top(state, 0, 0)).Code);

        var folded = Do(state, "fold"); // la mano termina y despues pasa la pausa
        var topAfter = Top(folded, 0, 150);
        var stackBefore = Parse(topAfter).Players[0].Stack;
        var next = Parse(NextHand(topAfter));
        Assert.Equal(0, next.Players[0].PendingChips);
        Assert.Equal(stackBefore + 150, next.Players[0].Stack + next.Players[0].Total); // la recarga se sumo a la pila
        Assert.Equal(350L, next.Players.Sum(p => p.Stack + p.Total));
    }

    [Fact]
    public void Someone_who_busted_can_come_back_with_a_top_up_or_is_removed_when_the_next_hand_starts()
    {
        var state = TwoSeated(100, 100);
        var first = Parse(state).Current;
        state = WithCards(state, new Dictionary<int, int[]> { [first] = [C(Spades, 14), C(Hearts, 14)], [1 - first] = [C(Spades, 13), C(Hearts, 13)] }, [C(Clubs, 2), C(Diamonds, 7), C(Clubs, 9), C(Diamonds, 3), C(Clubs, 4)]);
        var done = ShoveUntilDone(state);
        var loser = 1 - first;
        Assert.True(Parse(done).Players[loser].AwaitingRebuy);
        Assert.Equal(20d, Game.Turn(done)!.Seconds); // ventana para recargar

        // Recarga por debajo de la entrada minima: no vuelve. Con una valida, juega de inmediato y la pausa se acorta.
        Assert.Equal("bad_entry", Assert.Throws<TableRuleException>(() => Top(done, loser, 50)).Code);
        var back = Parse(Top(done, loser, 120));
        Assert.Equal(120L, back.Players[loser].Stack);
        Assert.False(back.Players[loser].AwaitingRebuy);
        Assert.Equal(3d, Game.Turn(Serialize(back))!.Seconds);
        Assert.Equal(2, Parse(NextHand(Serialize(back))).Players.Count(p => p.Cards.Count == 2));

        // Si no recarga, al empezar la mano siguiente se va con 0 y la mesa espera.
        var gone = Parse(NextHand(done));
        Assert.True(gone.Players[loser].Vacant);
        Assert.Contains(gone.CashOuts, c => c.Seat == loser && c.Chips == 0);
        Assert.True(gone.Waiting);
        Assert.Null(Game.Turn(Serialize(gone)));
    }

    [Fact]
    public void Leaving_between_hands_cashes_out_at_once_and_during_a_hand_waits_for_it_to_end()
    {
        var state = Sit(TwoSeated(), 2, 120); // el tercero mira la mano 1
        var s = Parse(state);

        // En plena mano: el que esta en ella sigue en automatico (se retira solo) y se va al terminar.
        var leaving = Parse(Leave(state, s.Current));
        Assert.True(leaving.Players[s.Current].LeaveAfterHand);
        Assert.True(Game.IsPassive(Serialize(leaving), s.Current));
        Assert.Empty(leaving.CashOuts); // todavia no se fue
        Assert.Equal("""{"type":"fold"}""", Game.BotAction(Serialize(leaving), s.Current));

        // El que miraba la mano (no puso nada) se va ya.
        var spectatorLeft = Leave(state, 2);
        var (afterDrain, outs) = Game.DrainCashOuts(spectatorLeft);
        Assert.Equal([new CashOut(2, 120)], outs);
        Assert.True(Parse(afterDrain).Players[2].Vacant);
        Assert.Empty(Parse(afterDrain).CashOuts); // ya se entrego: no se liquida dos veces

        // Al terminar la mano el que pidio levantarse cobra lo que le queda y la mesa sigue con los demas.
        var ended = Do(Serialize(leaving), "fold");
        var drained = Game.DrainCashOuts(NextHand(ended));
        var cashOut = Assert.Single(drained.CashOuts);
        Assert.Equal(s.Current, cashOut.Seat);
        Assert.Equal(98L, cashOut.Chips); // puso la ciega chica y se retiro: 100 - 2
        Assert.Equal(2, Parse(drained.State).HandNo); // paso la pausa y arranco otra mano (hay dos con fichas)
    }

    [Fact]
    public void When_not_enough_players_are_left_the_table_waits_and_starts_again_when_someone_sits()
    {
        var state = TwoSeated();
        var folded = Do(state, "fold");
        var left = Leave(folded, 0); // entre manos: se va ya
        var (afterLeave, outs) = Game.DrainCashOuts(left);
        Assert.Single(outs);

        var waiting = Parse(NextHand(afterLeave));
        Assert.True(waiting.Waiting); // queda uno solo con fichas
        Assert.Null(Game.Turn(Serialize(waiting)));

        var again = Parse(Sit(Serialize(waiting), 0, 100)); // se sienta otro: vuelve a haber dos y arranca sola
        Assert.False(again.Waiting);
        Assert.Equal(waiting.HandNo + 1, again.HandNo);
        Assert.NotNull(Game.Turn(Serialize(again)));
    }

    [Fact]
    public void Shutting_the_table_down_hands_everyone_their_chips_even_in_the_middle_of_a_hand()
    {
        var state = Sit(TwoSeated(100, 150), 2, 120);
        state = Do(state, "raise", 20); // plata en el pozo
        var mid = Parse(state);
        Assert.True(mid.Pot > 0);

        var (closed, outs) = Game.DrainCashOuts(Game.Shutdown(state));

        Assert.Equal(370L, outs.Sum(c => c.Chips)); // 100 + 150 + 120: lo puesto en la mano vuelve a su dueño
        Assert.Equal(3, outs.Count);
        var after = Parse(closed);
        Assert.All(after.Players, p => Assert.True(p.Vacant));
        Assert.True(after.Waiting);
        Assert.Null(Game.Turn(closed));
    }

    [Fact]
    public void A_cash_game_with_players_coming_and_going_never_loses_or_creates_chips()
    {
        for (var seed = 0; seed < 12; seed++)
        {
            var rng = new Random(seed);
            var state = StartCash(6);
            long entered = 0;
            long cashed = 0;
            for (var step = 0; step < 3_000; step++)
            {
                var s = Parse(state);
                var roll = rng.NextDouble();
                try
                {
                    if (roll < 0.04 && s.Players.Any(p => p.Vacant))
                    {
                        var seat = Enumerable.Range(0, 6).Where(i => s.Players[i].Vacant).ElementAt(rng.Next(s.Players.Count(p => p.Vacant)));
                        var chips = rng.NextInt64(80, 401);
                        state = Sit(state, seat, chips);
                        entered += chips;
                    }
                    else if (roll < 0.07)
                    {
                        var seated = Enumerable.Range(0, 6).Where(i => !s.Players[i].Vacant).ToList();
                        if (seated.Count > 0)
                        {
                            var seat = seated[rng.Next(seated.Count)];
                            var chips = rng.NextInt64(1, 200);
                            state = Top(state, seat, chips);
                            entered += chips;
                        }
                    }
                    else if (roll < 0.09)
                    {
                        var seated = Enumerable.Range(0, 6).Where(i => !s.Players[i].Vacant).ToList();
                        if (seated.Count > 0)
                        {
                            state = Leave(state, seated[rng.Next(seated.Count)]);
                        }
                    }
                }
                catch (TableRuleException)
                {
                    // Una jugada que las reglas rechazan (pila maxima, entrada fuera de rango...): no cambia nada.
                }

                var drained = Game.DrainCashOuts(state);
                state = drained.State;
                cashed += drained.CashOuts.Sum(c => c.Chips);

                if (Game.Turn(state) is { } turn)
                {
                    var current = Parse(state);
                    var action = turn.Automatic ? Game.TimeoutAction(state, turn.Seat) : PokerBot.Choose(current, turn.Seat);
                    state = Game.Apply(state, turn.Seat, action, DateTimeOffset.UnixEpoch);
                }

                var now = Parse(state);
                // Durante la mano las fichas estan en las pilas, lo recargado y el pozo; entre manos los pozos ya estan en las pilas.
                Assert.Equal(entered, now.Players.Sum(p => p.Stack + p.PendingChips) + (now.Done || now.Waiting ? 0 : now.Pot) + now.CashOuts.Sum(c => c.Chips) + cashed + now.RakeTotal);
            }

            // Se cierra la mesa: todo lo que se puso es lo que se llevaron mas el rake de la casa.
            var closing = Game.DrainCashOuts(Game.Shutdown(state));
            cashed += closing.CashOuts.Sum(c => c.Chips);
            Assert.Equal(entered, cashed + Parse(closing.State).RakeTotal);
        }
    }
}
