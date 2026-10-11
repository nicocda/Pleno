using System.Text.Json;
using Casino.Modules.Games.Tables;
using Casino.Modules.Games.Truco;

namespace Casino.Games.Tests;

/// <summary>Las reglas del Truco (maquina de estados pura): jerarquia de cartas, envido, truco, bazas y partidas completas de bots contra bots.</summary>
public sealed class TrucoTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TrucoGame Game = new();
    private static readonly Guid TableId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static readonly int[] ValidNumbers = [1, 2, 3, 4, 5, 6, 7, 10, 11, 12];

    private const int Swords = 0;
    private const int Clubs = 1;
    private const int Golds = 2;
    private const int Cups = 3;

    private static int C(int suit, int number) => TrucoCards.CardOf(suit, number);

    /// <summary>Una ronda armada a mano: las cartas de cada uno, quien es mano y de quien es el turno.</summary>
    private static TrucoState Hand(int[] seat0, int[] seat1, int mano = 0, int score0 = 0, int score1 = 0)
    {
        return new TrucoState
        {
            Seats = 2,
            BuyIn = 100,
            Scores = [score0, score1],
            Mano = mano,
            HandNo = 1,
            Current = mano,
            Dealt = [[.. seat0], [.. seat1]],
            Hands = [[.. seat0], [.. seat1]],
            Seed = "truco-test-seed",
            TableId = TableId.ToString("N"),
        };
    }

    private static string Serialize(TrucoState state) => JsonSerializer.Serialize(state, Json);

    private static TrucoState Parse(string state) => JsonSerializer.Deserialize<TrucoState>(state, Json)!;

    private static string Do(string state, int seat, string type, int? card = null) =>
        Game.Apply(state, seat, card is null ? $$"""{"type":"{{type}}"}""" : $$"""{"type":"{{type}}","card":{{card}}}""", DateTimeOffset.UnixEpoch);

    // Un reparto comodo: el mano (asiento 0) con buenas cartas y el otro con regulares.
    private static TrucoState Standard() => Hand([C(Swords, 1), C(Clubs, 7), C(Cups, 4)], [C(Swords, 3), C(Golds, 5), C(Clubs, 12)]);

    [Fact]
    public void The_deck_is_the_forty_cards_of_the_spanish_deck_and_each_has_its_number()
    {
        var deck = TrucoCards.NewDeck();

        Assert.Equal(40, deck.Distinct().Count());
        Assert.All(deck, c => Assert.Contains(TrucoCards.NumberOf(c), ValidNumbers));
        Assert.Equal(10, deck.Count(c => TrucoCards.SuitOf(c) == Swords));
        Assert.Equal(26, C(Golds, 7)); // oros (2) * 10 + posicion del 7 (6)
    }

    [Fact]
    public void Card_strength_follows_the_traditional_hierarchy()
    {
        int[] descending =
        [
            C(Swords, 1), C(Clubs, 1), C(Swords, 7), C(Golds, 7),
            C(Swords, 3), C(Clubs, 2), C(Cups, 1), C(Golds, 12), C(Cups, 11), C(Clubs, 10), C(Cups, 7), C(Golds, 6), C(Cups, 5), C(Clubs, 4),
        ];

        for (var i = 0; i < descending.Length - 1; i++)
        {
            Assert.True(TrucoCards.StrengthOf(descending[i]) > TrucoCards.StrengthOf(descending[i + 1]), $"la carta {i} deberia ganarle a la {i + 1}");
        }

        // Todas las cartas del mismo numero valen igual salvo las excepciones (1 y 7 de ciertos palos).
        Assert.Equal(TrucoCards.StrengthOf(C(Swords, 3)), TrucoCards.StrengthOf(C(Cups, 3)));
        Assert.Equal(TrucoCards.StrengthOf(C(Cups, 1)), TrucoCards.StrengthOf(C(Golds, 1)));
        Assert.Equal(TrucoCards.StrengthOf(C(Cups, 7)), TrucoCards.StrengthOf(C(Clubs, 7)));
        Assert.Equal(TrucoCards.StrengthOf(C(Golds, 6)), TrucoCards.StrengthOf(C(Swords, 6)));
    }

    [Theory]
    [InlineData(7, 0, 6, 0, 1, 2, 33)] // 7 y 6 de espadas + 1 de oros: 20 + 7 + 6
    [InlineData(7, 0, 6, 2, 1, 1, 7)] // sin dos del mismo palo: la carta mas alta
    [InlineData(12, 0, 11, 0, 5, 2, 20)] // dos figuras del mismo palo: 20 + 0 + 0
    [InlineData(12, 0, 5, 0, 4, 3, 25)] // figura y 5: 20 + 0 + 5
    [InlineData(7, 0, 6, 0, 5, 0, 33)] // tres del mismo palo: las dos mejores
    public void Envido_points_are_twenty_plus_the_best_two_of_a_suit_or_the_highest_card(int n1, int s1, int n2, int s2, int n3, int s3, int expected) =>
        Assert.Equal(expected, TrucoCards.EnvidoPoints([C(s1, n1), C(s2, n2), C(s3, n3)]));

    [Theory]
    [InlineData(new[] { 0, 0 }, 0, 0)]
    [InlineData(new[] { 0, 1, 0 }, 0, 0)]
    [InlineData(new[] { 0, 1, 1 }, 0, 1)]
    [InlineData(new[] { -1, 0 }, 0, 0)] // la primera fue parda: decide la segunda
    [InlineData(new[] { 1, -1 }, 0, 1)] // la segunda fue parda: gana quien gano la primera
    [InlineData(new[] { -1, -1, -1 }, 1, 1)] // todo parda: gana el mano
    [InlineData(new[] { -1, -1, 0 }, 1, 0)]
    [InlineData(new[] { 0, 1, -1 }, 1, 0)] // 1 a 1 y la tercera parda: gana quien gano la primera
    [InlineData(new[] { 1, 0, -1 }, 0, 1)]
    public void The_winner_of_a_hand_follows_the_rules_for_ties(int[] bazas, int mano, int expected) =>
        Assert.Equal(expected, TrucoGame.HandWinner(bazas, mano));

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 0 })]
    [InlineData(new[] { 0, 1 })]
    [InlineData(new[] { -1, -1 })]
    [InlineData(new[] { -1 })]
    public void A_hand_with_no_decision_yet_has_no_winner(int[] bazas) => Assert.Null(TrucoGame.HandWinner(bazas, 0));

    [Fact]
    public void Starting_deals_three_different_cards_to_each_and_is_deterministic()
    {
        var context = new GameStartContext(2, "start-seed", TableId, 100, DateTimeOffset.UnixEpoch);
        var state = Parse(Game.Start(context));

        Assert.All(state.Hands, h => Assert.Equal(3, h.Count));
        Assert.Equal(6, state.Hands.SelectMany(h => h).Distinct().Count());
        Assert.Equal(state.Mano, state.Current);
        Assert.Equal(Game.Start(context), Game.Start(context));
        Assert.NotEqual(Game.Start(context), Game.Start(context with { Seed = "another-seed" }));
        Assert.Equal(TrucoGame.TargetPoints, state.Target);
    }

    [Fact]
    public void The_stronger_card_wins_the_baza_and_the_winner_leads_the_next_one()
    {
        var state = Serialize(Standard());

        state = Do(state, 0, "play", C(Cups, 4)); // el mano sale con la mas baja
        Assert.Equal(1, Parse(state).Current);
        state = Do(state, 1, "play", C(Swords, 3)); // el 3 le gana al 4

        var after = Parse(state);
        Assert.Equal([1], after.Bazas);
        Assert.Equal(1, after.Current); // juega primero quien gano
        Assert.Equal(2, after.Hands[0].Count);
    }

    [Fact]
    public void Equal_strength_cards_are_a_tie_and_the_mano_leads_again()
    {
        var state = Serialize(Hand([C(Swords, 3), C(Clubs, 4), C(Cups, 5)], [C(Cups, 3), C(Golds, 4), C(Clubs, 12)]));

        state = Do(state, 0, "play", C(Swords, 3));
        state = Do(state, 1, "play", C(Cups, 3)); // dos 3: parda

        var after = Parse(state);
        Assert.Equal([-1], after.Bazas);
        Assert.Equal(0, after.Current); // el mano
    }

    [Fact]
    public void Winning_two_bazas_wins_the_hand_one_point_and_starts_the_next_hand_with_the_other_as_mano()
    {
        var state = Serialize(Standard());

        state = Do(state, 0, "play", C(Swords, 1));
        state = Do(state, 1, "play", C(Swords, 3)); // gana el mano con el ancho de espadas
        state = Do(state, 0, "play", C(Clubs, 7));
        state = Do(state, 1, "play", C(Golds, 5)); // gana el mano otra vez

        var after = Parse(state);
        Assert.Equal([1, 0], after.Scores);
        Assert.Equal((1, 2), (after.Mano, after.HandNo)); // empieza otra ronda: reparte de nuevo y el mano cambia
        Assert.Equal(1, after.Current);
        Assert.All(after.Hands, h => Assert.Equal(3, h.Count));
        Assert.Empty(after.Table);
    }

    [Fact]
    public void Truco_accepted_raises_the_hand_to_two_points_and_only_the_other_player_can_raise_again()
    {
        var state = Serialize(Standard());
        Assert.Contains("truco", ((TrucoView)Game.View(state, 0)).Actions);

        state = Do(state, 0, "truco");
        Assert.Equal(1, Parse(state).Current); // le toca responder al otro
        Assert.Contains("quiero", ((TrucoView)Game.View(state, 1)).Actions);
        state = Do(state, 1, "quiero");

        var accepted = Parse(state);
        Assert.Equal((1, 0), (accepted.TrucoLevel, accepted.Current)); // el que canto sigue con su jugada
        Assert.DoesNotContain("retruco", ((TrucoView)Game.View(state, 0)).Actions); // no puede subir lo que el mismo canto
        Assert.DoesNotContain("truco", ((TrucoView)Game.View(state, 0)).Actions);

        state = Do(state, 0, "play", C(Swords, 1));
        Assert.Contains("retruco", ((TrucoView)Game.View(state, 1)).Actions); // el otro si puede
    }

    [Fact]
    public void A_rejected_truco_gives_the_caller_one_point_and_a_rejected_retruco_gives_two()
    {
        var rejected = Parse(Do(Do(Serialize(Standard()), 0, "truco"), 1, "no_quiero"));
        Assert.Equal([1, 0], rejected.Scores);
        Assert.Equal(2, rejected.HandNo); // la ronda termino: se reparte de nuevo

        var state = Do(Serialize(Standard()), 0, "truco");
        state = Do(state, 1, "retruco"); // acepta el truco y sube
        var pending = Parse(state);
        Assert.Equal((1, 2, 0), (pending.Pending!.Caller, pending.Pending.Level, pending.Current));
        var retruco = Parse(Do(state, 0, "no_quiero"));
        Assert.Equal([0, 2], retruco.Scores); // el que subio se lleva los 2 del truco aceptado
    }

    [Fact]
    public void Retruco_and_vale_cuatro_can_be_accepted_and_each_raise_alternates_between_the_players()
    {
        var state = Do(Serialize(Standard()), 0, "truco");
        state = Do(state, 1, "retruco");
        state = Do(state, 0, "vale4"); // acepta el retruco y sube a vale cuatro
        Assert.DoesNotContain("retruco", ((TrucoView)Game.View(state, 1)).Actions);
        state = Do(state, 1, "quiero");

        var after = Parse(state);
        Assert.Equal((3, 0), (after.TrucoLevel, after.Current));
        Assert.DoesNotContain(((TrucoView)Game.View(Do(state, 0, "play", C(Swords, 1)), 1)).Actions, a => a is "truco" or "retruco" or "vale4"); // ya no hay mas para subir
    }

    [Fact]
    public void Going_to_the_mazo_gives_the_other_player_what_the_hand_was_worth()
    {
        var plain = Parse(Do(Serialize(Standard()), 0, "mazo"));
        Assert.Equal([0, 1], plain.Scores);

        var state = Do(Do(Serialize(Standard()), 0, "truco"), 1, "quiero"); // vale 2
        state = Do(state, 0, "play", C(Swords, 1));
        var folded = Parse(Do(state, 1, "mazo"));
        Assert.Equal([2, 0], folded.Scores);
    }

    [Fact]
    public void Envido_accepted_compares_the_points_and_the_mano_wins_ties()
    {
        // El mano (0) tiene 33; el otro 27.
        var state = Serialize(Hand([C(Swords, 7), C(Swords, 6), C(Golds, 4)], [C(Cups, 7), C(Cups, 12), C(Clubs, 4)]));
        state = Do(state, 0, "envido");
        state = Do(state, 1, "quiero");
        var after = Parse(state);
        Assert.Equal([2, 0], after.Scores);
        Assert.True(after.EnvidoDone);
        Assert.Equal(0, after.Current); // el que canto sigue con su jugada
        var result = after.Events.Single(e => e.Kind == "envido_result");
        Assert.Equal((0, 33, 27, 2), (result.Seat, result.A, result.B, result.Value)); // se muestran los puntos de los dos

        // Empate de puntos: gana el mano.
        var tie = Serialize(Hand([C(Swords, 7), C(Swords, 6), C(Golds, 4)], [C(Cups, 7), C(Cups, 6), C(Clubs, 4)], mano: 1));
        Assert.Equal([0, 2], Parse(Do(Do(tie, 1, "envido"), 0, "quiero")).Scores);
    }

    [Fact]
    public void Rejected_envido_gives_the_caller_what_was_accepted_before_the_last_call_and_at_least_one()
    {
        Assert.Equal([1, 0], Parse(Do(Do(Serialize(Standard()), 0, "envido"), 1, "no_quiero")).Scores);

        var state = Do(Do(Serialize(Standard()), 0, "envido"), 1, "envido"); // sube con otro envido
        Assert.Equal([0, 2], Parse(Do(state, 0, "no_quiero")).Scores); // el que subio se lleva los 2 del primero
        var falta = Do(Do(Serialize(Standard()), 0, "real_envido"), 1, "falta_envido"); // sube a falta envido
        Assert.Equal([0, 3], Parse(Do(falta, 0, "no_quiero")).Scores); // se lleva los 3 del real envido
    }

    [Fact]
    public void The_chain_of_envido_calls_follows_the_allowed_raises()
    {
        var state = Do(Serialize(Standard()), 0, "envido");
        Assert.Equal(["envido", "falta_envido", "no_quiero", "quiero", "real_envido"], ((TrucoView)Game.View(state, 1)).Actions);

        state = Do(state, 1, "envido");
        Assert.Equal(["falta_envido", "no_quiero", "quiero", "real_envido"], ((TrucoView)Game.View(state, 0)).Actions); // dos envidos ya no se pueden repetir

        state = Do(state, 0, "real_envido");
        Assert.Equal(["falta_envido", "no_quiero", "quiero"], ((TrucoView)Game.View(state, 1)).Actions);
        state = Do(state, 1, "falta_envido");
        Assert.Equal(["no_quiero", "quiero"], ((TrucoView)Game.View(state, 0)).Actions);
    }

    [Fact]
    public void Accepted_chain_adds_up_and_the_falta_envido_is_what_the_leader_still_needs()
    {
        var cards = (new[] { C(Swords, 7), C(Swords, 6), C(Golds, 4) }, new[] { C(Cups, 5), C(Clubs, 4), C(Golds, 12) });
        var chain = Serialize(Hand(cards.Item1, cards.Item2));
        chain = Do(Do(chain, 0, "envido"), 1, "envido");
        chain = Do(Do(chain, 0, "real_envido"), 1, "quiero"); // envido + envido + real = 2 + 2 + 3
        Assert.Equal(7, Parse(chain).Scores[0]);

        var falta = Serialize(Hand(cards.Item1, cards.Item2, score0: 4, score1: 9));
        falta = Do(Do(falta, 0, "falta_envido"), 1, "quiero");
        Assert.Equal(4 + (15 - 9), Parse(falta).Scores[0]); // lo que le falta al que va ganando (el de 9) para llegar a 15
    }

    [Fact]
    public void Envido_is_only_for_the_first_baza_before_a_truco_is_accepted_and_only_once()
    {
        var state = Serialize(Standard());
        Assert.Contains("envido", ((TrucoView)Game.View(state, 0)).Actions);

        // Se puede cantar tambien el segundo en jugar, antes de tirar su carta.
        var afterFirstCard = Do(state, 0, "play", C(Swords, 1));
        Assert.Contains("envido", ((TrucoView)Game.View(afterFirstCard, 1)).Actions);

        // Una vez jugada la primera baza ya no.
        var afterBaza = Do(afterFirstCard, 1, "play", C(Swords, 3));
        Assert.DoesNotContain("envido", ((TrucoView)Game.View(afterBaza, 1)).Actions);

        // Con un truco aceptado tampoco, y despues de un envido ya resuelto no se repite.
        var withTruco = Do(Do(state, 0, "truco"), 1, "quiero");
        Assert.DoesNotContain("envido", ((TrucoView)Game.View(withTruco, 0)).Actions);
        var done = Do(Do(state, 0, "envido"), 1, "quiero");
        Assert.DoesNotContain("envido", ((TrucoView)Game.View(done, 0)).Actions);
    }

    [Fact]
    public void While_a_call_waits_for_an_answer_the_only_options_are_to_answer_or_raise()
    {
        var state = Do(Serialize(Standard()), 0, "truco");

        Assert.Equal(["no_quiero", "quiero", "retruco"], ((TrucoView)Game.View(state, 1)).Actions);
        Assert.Equal("illegal", Assert.Throws<TableRuleException>(() => Do(state, 1, "play", C(Swords, 3))).Code);
        Assert.Equal("illegal", Assert.Throws<TableRuleException>(() => Do(state, 1, "mazo")).Code);
        Assert.Equal("not_your_turn", Assert.Throws<TableRuleException>(() => Do(state, 0, "quiero")).Code);
    }

    [Fact]
    public void Illegal_plays_are_rejected_with_a_reason()
    {
        var state = Serialize(Standard());

        Assert.Equal("not_in_hand", Assert.Throws<TableRuleException>(() => Do(state, 0, "play", C(Swords, 3))).Code);
        Assert.Equal("not_your_turn", Assert.Throws<TableRuleException>(() => Do(state, 1, "play", C(Swords, 3))).Code);
        Assert.Equal("illegal", Assert.Throws<TableRuleException>(() => Do(state, 0, "quiero")).Code);
        Assert.Equal("illegal", Assert.Throws<TableRuleException>(() => Do(state, 0, "retruco")).Code); // primero hay que cantar truco
        Assert.Equal("bad_action", Assert.Throws<TableRuleException>(() => Game.Apply(state, 0, "no es json", DateTimeOffset.UnixEpoch)).Code);
        Assert.Equal("bad_action", Assert.Throws<TableRuleException>(() => Do(state, 0, "play")).Code); // falta la carta
    }

    [Fact]
    public void Reaching_fifteen_wins_the_pot_and_ends_the_game()
    {
        var state = Serialize(Hand([C(Swords, 1), C(Clubs, 7), C(Cups, 4)], [C(Swords, 3), C(Golds, 5), C(Clubs, 12)], score0: 14));

        var finished = Do(state, 0, "mazo"); // el otro se lleva 1 punto: no llega
        Assert.Null(Game.Outcome(finished));

        var winning = Do(Serialize(Hand([C(Swords, 1), C(Clubs, 7), C(Cups, 4)], [C(Swords, 3), C(Golds, 5), C(Clubs, 12)], score0: 14, score1: 14)), 0, "mazo");
        Assert.Equal(1, Parse(winning).Winner);
        Assert.Null(Game.Turn(winning));
        Assert.Equal([0L, 200L], Game.Outcome(winning)!.Payouts);
        Assert.Equal("finished", Assert.Throws<TableRuleException>(() => Do(winning, 0, "play", 1)).Code);
    }

    [Fact]
    public void A_player_sees_only_their_own_cards_and_envido_points()
    {
        var state = Serialize(Standard());

        var mine = (TrucoView)Game.View(state, 1);
        Assert.Equal([C(Swords, 3), C(Golds, 5), C(Clubs, 12)], mine.Hand);
        Assert.Equal(3, mine.OpponentCards);
        Assert.Equal(TrucoCards.EnvidoPoints([C(Swords, 3), C(Golds, 5), C(Clubs, 12)]), mine.EnvidoPoints);
        Assert.Empty(mine.Actions); // no es su turno

        var spectator = (TrucoView)Game.View(state, null);
        Assert.Empty(spectator.Hand);
        Assert.Null(spectator.EnvidoPoints);

        var json = JsonSerializer.Serialize(mine, Json);
        Assert.DoesNotContain("seed", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dealt", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Bots_playing_against_bots_always_finish_a_game_with_a_valid_payout(int run)
    {
        for (var game = 0; game < 60; game++)
        {
            var state = Game.Start(new GameStartContext(2, $"bots-{run}-{game}", Guid.NewGuid(), 50, DateTimeOffset.UnixEpoch));
            var steps = 0;
            while (Game.Turn(state) is { } turn)
            {
                Assert.True(++steps < 3_000, "la partida no termina");
                state = Game.Apply(state, turn.Seat, Game.BotAction(state, turn.Seat), DateTimeOffset.UnixEpoch);
                var s = Parse(state);
                Assert.All(s.Hands, h => Assert.InRange(h.Count, 0, 3));
                Assert.All(s.Scores, score => Assert.True(score >= 0));
            }

            var payouts = Game.Outcome(state)!.Payouts;
            Assert.Equal([0L, 100L], payouts.Order());
            Assert.True(Parse(state).Scores.Max() >= TrucoGame.TargetPoints);
        }
    }

    [Fact]
    public void Players_who_always_time_out_still_finish_the_game()
    {
        var state = Game.Start(new GameStartContext(2, "timeouts", TableId, 50, DateTimeOffset.UnixEpoch));
        var steps = 0;
        while (Game.Turn(state) is { } turn)
        {
            Assert.True(++steps < 5_000, "la partida no termina");
            state = Game.Apply(state, turn.Seat, Game.TimeoutAction(state, turn.Seat), DateTimeOffset.UnixEpoch);
        }

        Assert.Equal(100L, Game.Outcome(state)!.Payouts.Sum());
    }

    [Fact]
    public void A_bot_asked_to_answer_a_call_gives_a_legal_answer()
    {
        var state = Do(Serialize(Standard()), 0, "envido");

        var answer = Game.BotAction(state, 1);

        Parse(Game.Apply(state, 1, answer, DateTimeOffset.UnixEpoch)); // no lanza: es legal
    }
}
