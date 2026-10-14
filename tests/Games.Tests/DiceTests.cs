using Casino.Modules.Games.Application;
using Casino.Modules.Games.Dice;

namespace Casino.Games.Tests;

public class DiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private const string ServerSeed = "9f2c4a7e1b3d58606a1f0e9d8c7b6a5f4e3d2c1b0a99887766554433221100ff";
    private const string ClientSeed = "pleno-lab";

    [Theory]
    [InlineData(DiceDirection.Under, 5000, 0, true)]
    [InlineData(DiceDirection.Under, 5000, 4999, true)]
    [InlineData(DiceDirection.Under, 5000, 5000, false)]
    [InlineData(DiceDirection.Over, 5000, 5000, true)]
    [InlineData(DiceDirection.Over, 5000, 9999, true)]
    [InlineData(DiceDirection.Over, 5000, 4999, false)]
    public void Wins_depends_on_direction_and_the_limit(DiceDirection direction, int chance, int roll, bool wins)
    {
        Assert.Equal(wins, DiceBet.Create(direction, chance, 100).Wins(roll));
    }

    [Theory]
    [InlineData(5000, 100, 196)] // x1,96
    [InlineData(200, 100, 4900)] // x49
    [InlineData(9500, 100, 103)] // 9800/9500 = 1,031: se redondea hacia abajo
    [InlineData(3333, 7, 20)] // 7*9800/3333 = 20,58
    public void A_win_pays_stake_times_9800_over_chance_rounded_down(int chance, long stake, long expected)
    {
        var bet = DiceBet.Create(DiceDirection.Under, chance, stake);

        Assert.Equal(expected, bet.PayoutFor(0));
        Assert.Equal(0, bet.PayoutFor(DiceBet.Rolls - 1));
    }

    [Fact]
    public void The_house_edge_is_two_percent_for_every_chance()
    {
        // Retorno esperado exacto sobre las 10.000 tiradas posibles, con una apuesta grande para que el redondeo casi no pese.
        foreach (var chance in new[] { 200, 1000, 2500, 4950, 7777, 9500 })
        {
            var bet = DiceBet.Create(DiceDirection.Under, chance, DiceBet.MaxStake);
            long returned = 0;
            for (var roll = 0; roll < DiceBet.Rolls; roll++)
            {
                returned += bet.PayoutFor(roll);
            }

            var rtp = returned / (double)(DiceBet.Rolls * DiceBet.MaxStake);
            Assert.InRange(rtp, 0.9795, 0.98);
        }
    }

    [Theory]
    [InlineData(199, 100)]
    [InlineData(9501, 100)]
    [InlineData(5000, 0)]
    [InlineData(5000, 10_001)]
    public void Out_of_range_bets_are_rejected(int chance, long stake)
    {
        Assert.False(DiceBet.TryCreate(DiceDirection.Over, chance, stake, out _));
    }

    [Fact]
    public void An_unknown_direction_is_rejected()
    {
        Assert.False(DiceBet.TryCreate((DiceDirection)7, 5000, 100, out _));
    }

    [Fact]
    public void Play_is_deterministic_and_stays_in_range()
    {
        var bet = DiceBet.Create(DiceDirection.Under, 5000, 100);

        var first = DiceGame.Play(bet, ServerSeed, ClientSeed, 0);
        var again = DiceGame.Play(bet, ServerSeed, ClientSeed, 0);

        Assert.Equal(first, again);
        for (var nonce = 0; nonce < 200; nonce++)
        {
            Assert.InRange(DiceGame.Play(bet, ServerSeed, ClientSeed, nonce).Roll, 0, DiceBet.Rolls - 1);
        }
    }

    [Fact]
    public void Over_and_under_with_the_same_chance_never_both_win_below_fifty_percent()
    {
        var under = DiceBet.Create(DiceDirection.Under, 4000, 100);
        var over = DiceBet.Create(DiceDirection.Over, 4000, 100);

        for (var roll = 0; roll < DiceBet.Rolls; roll++)
        {
            Assert.False(under.Wins(roll) && over.Wins(roll));
        }
    }

    private static DiceRound Placed() => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        Direction = DiceDirection.Under,
        Chance = 5000,
        Stake = 100,
        Status = RoundStatus.Placed,
        PlacedAt = Now,
    };

    [Fact]
    public void Happy_path_goes_placed_resolved_settled()
    {
        var round = Placed();

        Assert.True(round.MarkResolved(1234, 196));
        Assert.True(round.MarkSettled(Now));

        Assert.Equal((RoundStatus.Settled, 1234, 196L), (round.Status, round.Roll, round.Payout));
        Assert.True(round.IsClosed);
    }

    [Fact]
    public void Every_transition_is_idempotent_and_closed_rounds_do_not_move()
    {
        var round = Placed();
        round.MarkResolved(10, 196);

        Assert.False(round.MarkResolved(20, 0));
        Assert.True(round.MarkSettled(Now));
        Assert.False(round.MarkSettled(Now));
        Assert.False(round.MarkVoided("late", Now));
        Assert.False(round.MarkRejected("late", Now));
        Assert.Equal(10, round.Roll);
    }

    [Fact]
    public void A_rejected_or_voided_round_closes_without_paying()
    {
        var rejected = Placed();
        Assert.True(rejected.MarkRejected("InsufficientFunds", Now));
        Assert.True(rejected.IsClosed);

        var voided = Placed();
        voided.MarkResolved(10, 196);
        Assert.True(voided.MarkVoided("ReservationExpired", Now));
        Assert.Equal(RoundStatus.Voided, voided.Status);
        Assert.False(voided.MarkSettled(Now));
    }
}
