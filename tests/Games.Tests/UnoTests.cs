using System.Text.Json;
using Casino.Modules.Games.Tables;
using Casino.Modules.Games.Uno;

namespace Casino.Games.Tests;

/// <summary>Las reglas de Uno (maquina de estados pura), el barajado provably fair y partidas completas de bots contra bots.</summary>
public sealed class UnoTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly UnoGame Game = new();
    private static readonly Guid TableId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    // Cartas: 0..24 rojas, 25..49 amarillas, 50..74 verdes, 75..99 azules (dentro de cada color: 0 = cero, 1..9 numeros, 19-20 salto, 21-22 reversa, 23-24 +2).
    private const int Red5 = 5;
    private const int Red7 = 7;
    private const int RedSkip = 19;
    private const int RedReverse = 21;
    private const int RedDrawTwo = 23;
    private const int Yellow5 = 30;
    private const int Green3 = 53;
    private const int Blue3 = 78;
    private const int Wild = 100;
    private const int WildDrawFour = 104;

    private static UnoState Blank(int seats, int top = Red7, int current = 0)
    {
        var state = new UnoState
        {
            Seats = seats,
            BuyIn = 100,
            Draw = [1, 2, 3, 4, 6, 8, 9, 10, 11, 12, 13, 14, 15],
            Discard = [top],
            TopColor = UnoCards.ColorOf(top),
            Current = current,
            Seed = "unit-test-seed",
            TableId = TableId.ToString("N"),
        };
        for (var i = 0; i < seats; i++)
        {
            state.Hands.Add([]);
        }

        return state;
    }

    private static string Serialize(UnoState state) => JsonSerializer.Serialize(state, Json);

    private static UnoState Parse(string state) => JsonSerializer.Deserialize<UnoState>(state, Json)!;

    private static string Play(string state, int seat, int card, int? color = null) =>
        Game.Apply(state, seat, color is null ? $$"""{"type":"play","card":{{card}}}""" : $$"""{"type":"play","card":{{card}},"color":{{color}}}""", DateTimeOffset.UnixEpoch);

    private static string Act(string state, int seat, string type) => Game.Apply(state, seat, $$"""{"type":"{{type}}"}""", DateTimeOffset.UnixEpoch);

    [Fact]
    public void The_deck_has_the_108_cards_of_the_game()
    {
        var kinds = UnoCards.NewDeck().GroupBy(UnoCards.KindOf).ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(108, UnoCards.NewDeck().Distinct().Count());
        Assert.Equal(76, kinds[UnoKind.Number]); // un 0 y dos de cada 1 a 9 por color
        Assert.Equal(8, kinds[UnoKind.Skip]);
        Assert.Equal(8, kinds[UnoKind.Reverse]);
        Assert.Equal(8, kinds[UnoKind.DrawTwo]);
        Assert.Equal(4, kinds[UnoKind.Wild]);
        Assert.Equal(4, kinds[UnoKind.WildDrawFour]);
        Assert.Equal(4, UnoCards.NewDeck().Count(c => UnoCards.NumberOf(c) == 0));
        Assert.Equal(8, UnoCards.NewDeck().Count(c => UnoCards.NumberOf(c) == 7));
    }

    [Fact]
    public void The_shuffle_matches_vectors_from_an_independent_python_implementation()
    {
        var twenty = Enumerable.Range(0, 20).ToList();
        FairShuffle.Shuffle(twenty, "tables-test-seed", "test-label");
        Assert.Equal([5, 4, 19, 13, 14, 8, 3, 7, 0, 16, 11, 17, 18, 2, 12, 10, 1, 15, 6, 9], twenty);

        var deck = UnoCards.NewDeck();
        FairShuffle.Shuffle(deck, "tables-test-seed", "uno:00000000000000000000000000000001");
        Assert.Equal([69, 52, 95, 93, 32, 25, 41, 83, 8, 90], deck.Take(10));
        Assert.Equal(108, deck.Distinct().Count());

        Assert.Equal(2, FairShuffle.Pick("tables-test-seed", "pick-label", 6));
        Assert.Equal(454, FairShuffle.Pick("tables-test-seed", "pick-label2", 1000));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    public void Starting_deals_seven_cards_each_and_the_whole_deck_is_accounted_for(int seats)
    {
        var state = Parse(Game.Start(new GameStartContext(seats, "start-seed", TableId, 100, DateTimeOffset.UnixEpoch)));

        Assert.All(state.Hands, hand => Assert.True(hand.Count >= 7)); // 7, o mas si la carta inicial era un +2 y le toco a alguien
        var all = state.Hands.SelectMany(h => h).Concat(state.Draw).Concat(state.Discard).ToList();
        Assert.Equal(108, all.Count);
        Assert.Equal(108, all.Distinct().Count());
        Assert.False(UnoCards.IsWild(state.Discard[0])); // la primera carta nunca es un comodin
        Assert.InRange(state.Current, 0, seats - 1);
    }

    [Fact]
    public void Starting_is_deterministic_for_a_seed_and_different_for_another()
    {
        var context = new GameStartContext(3, "deterministic-seed", TableId, 100, DateTimeOffset.UnixEpoch);

        Assert.Equal(Game.Start(context), Game.Start(context));
        Assert.NotEqual(Game.Start(context), Game.Start(context with { Seed = "another-seed" }));
        Assert.NotEqual(Game.Start(context), Game.Start(context with { TableId = Guid.NewGuid() }));
    }

    [Fact]
    public void A_card_is_playable_by_color_or_by_number_or_kind_and_wilds_follow_their_own_rules()
    {
        var state = Blank(2, top: Red7);
        state.Hands[0] = [Red5, Yellow5, Green3, Blue3 + 4 /* azul 7 */, RedSkip, Wild, WildDrawFour];
        var view = (UnoView)Game.View(Serialize(state), 0);

        Assert.Contains(Red5, view.Playable); // mismo color
        Assert.Contains(Blue3 + 4, view.Playable); // mismo numero (7)
        Assert.Contains(RedSkip, view.Playable); // mismo color
        Assert.Contains(Wild, view.Playable);
        Assert.DoesNotContain(Yellow5, view.Playable); // otro color y otro numero
        Assert.DoesNotContain(Green3, view.Playable);
        Assert.DoesNotContain(WildDrawFour, view.Playable); // tiene cartas rojas: el +4 no se puede
    }

    [Fact]
    public void A_wild_draw_four_can_only_be_played_without_cards_of_the_current_color()
    {
        var state = Blank(2, top: Red7);
        state.Hands[0] = [Green3, WildDrawFour];
        state.Hands[1] = [Blue3];

        var after = Parse(Play(Serialize(state), 0, WildDrawFour, color: 2));

        Assert.Equal(2, after.TopColor);
        Assert.Equal(5, after.Hands[1].Count); // roba 4
        Assert.Equal(0, after.Current); // y pierde el turno: con dos jugadores vuelve a jugar el mismo
    }

    [Fact]
    public void Playing_a_number_passes_the_turn_and_becomes_the_top_card()
    {
        var state = Blank(3, top: Red7);
        state.Hands[0] = [Red5, Green3];

        var after = Parse(Play(Serialize(state), 0, Red5));

        Assert.Equal((Red5, 0, 1), (after.Discard[^1], UnoCards.ColorOf(after.Discard[^1]), after.Current));
        Assert.Equal([Green3], after.Hands[0]);
    }

    [Fact]
    public void Skip_passes_over_the_next_player_and_reverse_changes_direction()
    {
        var skip = Blank(3);
        skip.Hands[0] = [RedSkip, Green3];
        Assert.Equal(2, Parse(Play(Serialize(skip), 0, RedSkip)).Current);

        var reverse = Blank(3);
        reverse.Hands[0] = [RedReverse, Green3];
        var after = Parse(Play(Serialize(reverse), 0, RedReverse));
        Assert.Equal((-1, 2), (after.Direction, after.Current)); // ahora se juega al reves: del 0 pasa al 2
    }

    [Fact]
    public void With_two_players_a_reverse_acts_as_a_skip()
    {
        var state = Blank(2);
        state.Hands[0] = [RedReverse, Green3];

        Assert.Equal(0, Parse(Play(Serialize(state), 0, RedReverse)).Current);
    }

    [Fact]
    public void Draw_two_makes_the_next_player_draw_and_lose_the_turn()
    {
        var state = Blank(3);
        state.Hands[0] = [RedDrawTwo, Green3];
        state.Hands[1] = [Blue3];

        var after = Parse(Play(Serialize(state), 0, RedDrawTwo));

        Assert.Equal(3, after.Hands[1].Count);
        Assert.Equal(2, after.Current);
    }

    [Fact]
    public void A_wild_needs_a_color_and_sets_it()
    {
        var state = Blank(2);
        state.Hands[0] = [Wild, Green3];

        var ex = Assert.Throws<TableRuleException>(() => Play(Serialize(state), 0, Wild));
        Assert.Equal("color_required", ex.Code);
        Assert.Equal(3, Parse(Play(Serialize(state), 0, Wild, color: 3)).TopColor);
    }

    [Fact]
    public void Drawing_a_playable_card_lets_the_player_play_it_or_pass_and_an_unplayable_one_ends_the_turn()
    {
        var playable = Blank(2);
        playable.Hands[0] = [Green3];
        playable.Draw = [Red5]; // la proxima carta es roja: se puede jugar sobre el 7 rojo
        var drew = Parse(Act(Serialize(playable), 0, "draw"));
        Assert.Equal((Red5, 0), (drew.DrawnCard, drew.Current)); // sigue siendo su turno
        Assert.Equal("drawn_only", Assert.Throws<TableRuleException>(() => Play(Serialize(drew), 0, Green3)).Code); // solo puede jugar la que robo o pasar
        Assert.Equal(1, Parse(Act(Serialize(drew), 0, "pass")).Current);
        Assert.Equal(1, Parse(Play(Serialize(drew), 0, Red5)).Current);

        var unplayable = Blank(2);
        unplayable.Hands[0] = [Green3];
        unplayable.Draw = [Blue3]; // azul 3 sobre el 7 rojo: no se puede
        var after = Parse(Act(Serialize(unplayable), 0, "draw"));
        Assert.Equal((1, null), (after.Current, after.DrawnCard));
        Assert.Equal(2, after.Hands[0].Count);
    }

    [Fact]
    public void Cannot_pass_without_drawing_or_draw_twice()
    {
        var state = Blank(2);
        state.Hands[0] = [Green3];

        Assert.Equal("cannot_pass", Assert.Throws<TableRuleException>(() => Act(Serialize(state), 0, "pass")).Code);
        state.Draw = [Red5];
        var drew = Serialize(Parse(Act(Serialize(state), 0, "draw")));
        Assert.Equal("already_drew", Assert.Throws<TableRuleException>(() => Act(drew, 0, "draw")).Code);
    }

    [Fact]
    public void Illegal_plays_are_rejected_with_a_reason()
    {
        var state = Blank(2);
        state.Hands[0] = [Green3, Yellow5];
        state.Hands[1] = [Red5];

        Assert.Equal("illegal", Assert.Throws<TableRuleException>(() => Play(Serialize(state), 0, Green3)).Code);
        Assert.Equal("not_in_hand", Assert.Throws<TableRuleException>(() => Play(Serialize(state), 0, Red5)).Code);
        Assert.Equal("not_your_turn", Assert.Throws<TableRuleException>(() => Play(Serialize(state), 1, Red5)).Code);
        Assert.Equal("bad_action", Assert.Throws<TableRuleException>(() => Game.Apply(Serialize(state), 0, "no es json", DateTimeOffset.UnixEpoch)).Code);
        Assert.Equal("bad_action", Assert.Throws<TableRuleException>(() => Game.Apply(Serialize(state), 0, """{"type":"fly"}""", DateTimeOffset.UnixEpoch)).Code);
    }

    [Fact]
    public void Emptying_the_hand_wins_the_pot_and_ends_the_game()
    {
        var state = Blank(3);
        state.Hands[0] = [Red5];
        state.Hands[1] = [Green3];
        state.Hands[2] = [Blue3];

        var finished = Play(Serialize(state), 0, Red5);

        Assert.Equal(0, Parse(finished).Winner);
        Assert.Null(Game.Turn(finished));
        Assert.Equal([300L, 0L, 0L], Game.Outcome(finished)!.Payouts); // se lleva las 3 entradas
        Assert.Null(Game.Outcome(Serialize(state)));
        Assert.Equal("finished", Assert.Throws<TableRuleException>(() => Act(finished, 1, "draw")).Code);
    }

    [Fact]
    public void When_the_draw_pile_runs_out_the_discard_is_reshuffled_keeping_the_top_card()
    {
        var state = Blank(2);
        state.Draw = [];
        state.Discard = [1, 2, 3, 4, Red7];
        state.Hands[0] = [Green3];
        state.Hands[1] = [Blue3];

        var after = Parse(Act(Serialize(state), 0, "draw"));

        Assert.Equal([Red7], after.Discard);
        Assert.Equal(1, after.Reshuffles);
        Assert.Equal((3, 2), (after.Draw.Count, after.Hands[0].Count)); // las 4 del descarte pasan al mazo y una se roba
        Assert.Equal(Act(Serialize(state), 0, "draw"), Act(Serialize(state), 0, "draw")); // verificable: la semilla fija el resultado
    }

    [Fact]
    public void A_player_sees_only_their_own_hand_and_the_counts_of_the_others()
    {
        var state = Blank(3);
        state.Hands[0] = [Red5, Green3];
        state.Hands[1] = [Blue3, Wild, Yellow5];
        state.Hands[2] = [RedSkip];

        var mine = (UnoView)Game.View(Serialize(state), 1);
        Assert.Equal([Blue3, Wild, Yellow5], mine.Hand);
        Assert.Equal([2, 3, 1], mine.Players.Select(p => p.Cards));
        Assert.Empty(Game.View(Serialize(state), null) is UnoView spectator ? spectator.Hand : [-1]);

        // Lo que se serializa para el navegador no contiene las cartas de los demas ni el mazo.
        var json = JsonSerializer.Serialize(mine, Json);
        Assert.DoesNotContain("\"draw\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("seed", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(state.Draw.Count, mine.DrawCount);
    }

    [Fact]
    public void Timeout_and_bot_actions_are_always_legal()
    {
        var state = Blank(2);
        state.Hands[0] = [Green3, Yellow5];

        Assert.Equal("""{"type":"draw"}""", Game.TimeoutAction(Serialize(state), 0));
        Assert.Equal("""{"type":"draw"}""", Game.BotAction(Serialize(state), 0)); // nada para jugar: roba
        Parse(Game.Apply(Serialize(state), 0, Game.TimeoutAction(Serialize(state), 0), DateTimeOffset.UnixEpoch));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    public void Bots_playing_against_bots_always_finish_with_a_valid_payout(int seats)
    {
        for (var game = 0; game < 40; game++)
        {
            var state = Game.Start(new GameStartContext(seats, $"bots-{seats}-{game}", Guid.NewGuid(), 50, DateTimeOffset.UnixEpoch));
            var steps = 0;
            while (Game.Turn(state) is { } turn)
            {
                Assert.True(++steps < 5_000, "la partida no termina");
                var seat = turn.Seat;
                state = Game.Apply(state, seat, Game.BotAction(state, seat), DateTimeOffset.UnixEpoch);
                var all = Parse(state);
                Assert.Equal(108, all.Hands.SelectMany(h => h).Concat(all.Draw).Concat(all.Discard).Distinct().Count()); // nunca se pierde ni se duplica una carta
            }

            var payouts = Game.Outcome(state)!.Payouts;
            Assert.Equal(50L * seats, payouts.Sum());
            Assert.Single(payouts, p => p > 0);
        }
    }

    [Fact]
    public void Absent_players_who_keep_timing_out_are_taken_over_by_a_bot_and_the_game_still_finishes()
    {
        // Igual que la plataforma: un turno vencido juega "robar o pasar"; con 3 seguidos el asiento queda ausente y lo juega un bot.
        var state = Game.Start(new GameStartContext(3, "timeouts", TableId, 50, DateTimeOffset.UnixEpoch));
        var misses = new int[3];
        var steps = 0;
        while (Game.Turn(state) is { } turn)
        {
            Assert.True(++steps < 20_000, "la partida no termina");
            var away = misses[turn.Seat] >= 3;
            if (!away)
            {
                misses[turn.Seat]++;
            }

            state = Game.Apply(state, turn.Seat, away ? Game.BotAction(state, turn.Seat) : Game.TimeoutAction(state, turn.Seat), DateTimeOffset.UnixEpoch);
        }

        Assert.Equal(150L, Game.Outcome(state)!.Payouts.Sum());
    }
}
