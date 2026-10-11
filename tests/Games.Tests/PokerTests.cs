using System.Text.Json;
using Casino.Modules.Games.Poker;
using Casino.Modules.Games.Tables;

namespace Casino.Games.Tests;

/// <summary>Las reglas de Texas Hold'em (maquina de estados pura): evaluacion de manos, ciegas, rondas de apuestas, showdown, pagos y manos completas de bots contra bots.</summary>
public sealed class PokerTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly PokerGame Game = new();
    private static readonly Guid TableId = Guid.Parse("00000000-0000-0000-0000-000000000001");

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

        Assert.True(Parse(state).Done);
        Assert.Equal(300L, Game.Outcome(state)!.Payouts.Sum());
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
        Assert.Empty(done.Showdown); // nadie muestra nada
        var payouts = Game.Outcome(state)!.Payouts;
        Assert.Equal(300L, payouts.Sum());
        Assert.Equal(102, payouts[start.BigBlindSeat]); // gana las ciegas: 100 - 4 + 6
        Assert.Equal(98, payouts[start.SmallBlindSeat]); // perdio su ciega chica
        Assert.Equal(100, payouts[(start.BigBlindSeat + 1) % 3]); // el que se retiro sin poner nada
        Assert.Null(Game.Turn(state));
        Assert.Equal("finished", Assert.Throws<TableRuleException>(() => Game.Apply(state, 0, """{"type":"fold"}""", DateTimeOffset.UnixEpoch)).Code);
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
        Assert.Equal([200L, 0L], first == 0 ? Game.Outcome(state)!.Payouts : Game.Outcome(state)!.Payouts.Reverse());
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
        var payouts = Game.Outcome(state)!.Payouts;
        Assert.Equal(303L, payouts.Sum());
        Assert.Equal(101L, payouts.Min()); // nadie gana ni pierde
        Assert.Equal(101L, payouts.Max());
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

        var payouts = Game.Outcome(state)!.Payouts;
        Assert.Equal(303L, payouts.Sum());
        Assert.Equal(152L, payouts[order[0]]); // 151 + el resto de 1: el primero a la izquierda del boton
        Assert.Equal(151L, payouts[order[1]]);
        Assert.Equal(0L, payouts[order[2]]);
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
        Assert.DoesNotContain("seed", json, StringComparison.OrdinalIgnoreCase);
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
    public void Bots_playing_against_bots_always_finish_a_hand_with_a_valid_payout(int seats)
    {
        for (var hand = 0; hand < 80; hand++)
        {
            var state = Game.Start(new GameStartContext(seats, $"bots-{seats}-{hand}", Guid.NewGuid(), 200, DateTimeOffset.UnixEpoch));
            var steps = 0;
            while (Game.Turn(state) is { } turn)
            {
                Assert.True(++steps < 500, "la mano no termina");
                state = Game.Apply(state, turn.Seat, Game.BotAction(state, turn.Seat), DateTimeOffset.UnixEpoch);
                var s = Parse(state);
                Assert.All(s.Players, p => Assert.True(p.Stack >= 0 && p.Total <= 200 && p.Total == 200 - p.Stack));
            }

            var payouts = Game.Outcome(state)!.Payouts;
            Assert.Equal(200L * seats, payouts.Sum());
            Assert.All(payouts, p => Assert.True(p >= 0));
        }
    }

    [Fact]
    public void Players_who_always_time_out_still_finish_the_hand()
    {
        var state = StartHand(4);
        var steps = 0;
        while (Game.Turn(state) is { } turn)
        {
            Assert.True(++steps < 200, "la mano no termina");
            state = Game.Apply(state, turn.Seat, Game.TimeoutAction(state, turn.Seat), DateTimeOffset.UnixEpoch);
        }

        Assert.Equal(400L, Game.Outcome(state)!.Payouts.Sum());
        Assert.Equal("""{"type":"fold"}""", Game.TimeoutAction(StartHand(3), Parse(StartHand(3)).Current)); // con algo por pagar, se retira
    }
}
