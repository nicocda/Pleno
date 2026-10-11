using Casino.Modules.Games.Application;
using Casino.Modules.Games.Blackjack;

namespace Casino.Games.Tests;

/// <summary>Las reglas y el mazo de Blackjack: valores de mano, resultados, pagos y el zapato provably fair (contra una implementacion independiente).</summary>
public sealed class BlackjackTests
{
    // Cartas por rango (el palo no importa para los puntos): 0 = as, 1 a 8 = 2 a 9, 9 = 10, 10 = J, 11 = Q, 12 = K.
    private const int Ace = 0;
    private const int Two = 1;
    private const int Five = 4;
    private const int Six = 5;
    private const int Seven = 6;
    private const int Nine = 8;
    private const int Ten = 9;
    private const int King = 12;

    [Fact]
    public void Aces_count_as_eleven_unless_that_busts_and_the_hand_knows_when_it_is_soft()
    {
        Assert.Equal(new HandValue(21, true), BlackjackMath.Evaluate([Ace, King]));
        Assert.Equal(new HandValue(12, true), BlackjackMath.Evaluate([Ace, Ace])); // un as vale 11 y el otro 1
        Assert.Equal(new HandValue(13, false), BlackjackMath.Evaluate([Ace, Ace, King, Ace])); // los tres ases valen 1
    }

    [Fact]
    public void Hand_values_follow_the_usual_rules()
    {
        Assert.Equal(new HandValue(17, true), BlackjackMath.Evaluate([Ace, Six]));
        Assert.Equal(new HandValue(17, false), BlackjackMath.Evaluate([Ace, Six, King]));
        Assert.Equal(new HandValue(20, false), BlackjackMath.Evaluate([King, 10]));
        Assert.Equal(new HandValue(22, false), BlackjackMath.Evaluate([King, Seven, Five]));
        Assert.True(BlackjackMath.IsBust([King, Seven, Five]));
        Assert.True(BlackjackMath.IsBlackjack([Ace, Ten]));
        Assert.False(BlackjackMath.IsBlackjack([Seven, Seven, Seven])); // 21 con tres cartas no es blackjack natural
    }

    [Fact]
    public void The_dealer_draws_below_seventeen_and_stands_on_every_seventeen_including_soft()
    {
        Assert.True(BlackjackMath.DealerMustDraw([King, Six]));
        Assert.False(BlackjackMath.DealerMustDraw([King, Seven]));
        Assert.False(BlackjackMath.DealerMustDraw([Ace, Six])); // 17 blando: planta
        Assert.True(BlackjackMath.DealerMustDraw([Ace, Five])); // 16 blando: pide
    }

    [Fact]
    public void Results_against_the_dealer()
    {
        int[] dealer19 = [King, Nine];
        Assert.Equal(HandResult.Bust, BlackjackMath.Resolve([King, Seven, Five], [King, Six])); // pasarse pierde aunque el crupier tambien se pase despues
        Assert.Equal(HandResult.Blackjack, BlackjackMath.Resolve([Ace, King], dealer19));
        Assert.Equal(HandResult.Push, BlackjackMath.Resolve([Ace, King], [Ace, Ten])); // blackjack contra blackjack
        Assert.Equal(HandResult.Lose, BlackjackMath.Resolve([Seven, Seven, Seven], [Ace, King])); // 21 de tres cartas pierde contra blackjack natural
        Assert.Equal(HandResult.Win, BlackjackMath.Resolve([King, Ten], dealer19 /* 20 > 19 */));
        Assert.Equal(HandResult.Win, BlackjackMath.Resolve([King, Two], [King, Six, Nine])); // el crupier se paso
        Assert.Equal(HandResult.Push, BlackjackMath.Resolve([King, Nine], dealer19));
        Assert.Equal(HandResult.Lose, BlackjackMath.Resolve([King, Seven], dealer19));
    }

    [Theory]
    [InlineData(HandResult.Blackjack, 100, 250)] // apuesta + 3 a 2
    [InlineData(HandResult.Blackjack, 15, 37)] // 15 + piso(22,5) = 37: siempre un entero
    [InlineData(HandResult.Win, 100, 200)]
    [InlineData(HandResult.Push, 100, 100)]
    [InlineData(HandResult.Lose, 100, 0)]
    [InlineData(HandResult.Bust, 100, 0)]
    public void Payouts_are_whole_chips_and_include_the_stake(HandResult result, long stake, long expected) =>
        Assert.Equal(expected, BlackjackMath.PayoutFor(stake, result));

    [Fact]
    public void The_shoe_is_six_shuffled_decks_and_is_deterministic()
    {
        var round = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var shoe = BlackjackMath.Shoe("blackjack-test-seed", round);

        Assert.Equal(312, shoe.Length);
        Assert.All(Enumerable.Range(0, 52), card => Assert.Equal(6, shoe.Count(c => c == card)));
        Assert.Equal(shoe, BlackjackMath.Shoe("blackjack-test-seed", round));
        Assert.NotEqual(shoe, BlackjackMath.Shoe("otra-semilla", round));
        Assert.NotEqual(shoe, BlackjackMath.Shoe("blackjack-test-seed", Guid.Parse("00000000-0000-0000-0000-000000000002")));
    }

    [Theory]
    [InlineData("blackjack-test-seed", "00000000-0000-0000-0000-000000000001", new[] { 0, 33, 7, 17, 6, 50, 45, 24, 22, 42, 40, 49 }, new[] { 42, 26, 23 }, 246087)]
    [InlineData("9f2c0000000000000000000000000000000000000000000000000000000000ff", "0190a1b2-c3d4-7e5f-8091-a2b3c4d5e6f7", new[] { 44, 10, 1, 22, 35, 12, 0, 34, 18, 42, 4, 27 }, new[] { 19, 4, 11 }, 261287)]
    public void The_shoe_matches_vectors_from_an_independent_python_implementation(string seed, string round, int[] firstCards, int[] lastCards, int checksum)
    {
        var shoe = BlackjackMath.Shoe(seed, Guid.Parse(round));

        Assert.Equal(firstCards, shoe.Take(firstCards.Length));
        Assert.Equal(lastCards, shoe.TakeLast(lastCards.Length));
        Assert.Equal(checksum, shoe.Select((card, i) => (long)i * card).Sum() % 1_000_003);
    }

    [Fact]
    public void Dealing_positions_use_the_cards_in_the_documented_order()
    {
        // 2 jugadores: cartas 0 y 1 a los jugadores, 2 al crupier (visible), 3 y 4 segundas de los jugadores, 5 la tapada, el resto para pedir.
        Assert.Equal([0, 1], new[] { BlackjackMath.PlayerFirstCard(0), BlackjackMath.PlayerFirstCard(1) });
        Assert.Equal(2, BlackjackMath.DealerUpCard(2));
        Assert.Equal([3, 4], new[] { BlackjackMath.PlayerSecondCard(2, 0), BlackjackMath.PlayerSecondCard(2, 1) });
        Assert.Equal(5, BlackjackMath.DealerHoleCard(2));
        Assert.Equal(6, BlackjackMath.FirstFreeCard(2));
    }

    [Fact]
    public void A_bet_follows_its_hand_hitting_to_twenty_one_stands_and_busting_ends_it()
    {
        var bet = new BlackjackBet { Stake = 100, Status = RoundStatus.Placed, Reserved = true };
        Assert.True(bet.Deal(1, King, Six));
        Assert.Equal(HandState.Playing, bet.Hand);
        Assert.True(bet.Hit(Five)); // 21: se planta sola
        Assert.Equal(HandState.Stood, bet.Hand);
        Assert.False(bet.Hit(Two)); // ya no puede pedir

        var natural = new BlackjackBet { Stake = 100, Status = RoundStatus.Placed, Reserved = true };
        natural.Deal(1, Ace, King);
        Assert.Equal(HandState.Blackjack, natural.Hand);
        Assert.False(natural.Stand());

        var busted = new BlackjackBet { Stake = 100, Status = RoundStatus.Placed, Reserved = true };
        busted.Deal(1, King, Six);
        busted.Hit(Seven);
        Assert.Equal(HandState.Bust, busted.Hand);
        Assert.True(busted.Finish(HandResult.Bust));
        Assert.Equal(0, busted.Payout);
        Assert.False(busted.Finish(HandResult.Win)); // idempotente: ya esta resuelta
    }

    [Fact]
    public void Options_define_default_tables_and_reject_nonsense()
    {
        var options = new BlackjackOptions { Tables = [.. BlackjackOptions.DefaultTables] };
        options.Validate();
        Assert.NotNull(options.FindTable("mesa-1"));
        Assert.Null(options.FindTable("inexistente"));

        Assert.Throws<InvalidOperationException>(() => new BlackjackOptions { MaxSeats = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new BlackjackOptions { Tables = [new BlackjackTableConfig { Id = "a b" }] }.Validate());
        Assert.Throws<InvalidOperationException>(() => new BlackjackOptions { Tables = [new BlackjackTableConfig { Id = "a", MinStake = 10, MaxStake = 5 }] }.Validate());
        Assert.Throws<InvalidOperationException>(() => new BlackjackOptions { Tables = [new BlackjackTableConfig { Id = "a" }, new BlackjackTableConfig { Id = "a" }] }.Validate());
    }
}
