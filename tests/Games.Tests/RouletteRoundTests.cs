using Casino.Modules.Games.Application;
using Casino.Modules.Games.Roulette;

namespace Casino.Games.Tests;

public class RouletteRoundTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static RouletteRound Placed() => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        BetType = RouletteBetType.Red,
        Stake = 100,
        Status = RoundStatus.Placed,
        PlacedAt = Now,
    };

    [Fact]
    public void Happy_path_goes_placed_resolved_settled()
    {
        var round = Placed();

        Assert.True(round.MarkResolved(17, 200));
        Assert.Equal((RoundStatus.Resolved, 17, 200L), (round.Status, round.WinningNumber, round.Payout));
        Assert.True(round.MarkSettled(Now));

        Assert.Equal(RoundStatus.Settled, round.Status);
        Assert.True(round.IsClosed);
        Assert.Equal(Now, round.ClosedAt);
    }

    [Fact]
    public void Every_transition_is_idempotent_so_duplicate_deliveries_change_nothing()
    {
        var round = Placed();
        round.MarkResolved(17, 200);

        Assert.False(round.MarkResolved(5, 0));
        Assert.Equal((17, 200L), (round.WinningNumber, round.Payout));

        round.MarkSettled(Now);
        Assert.False(round.MarkSettled(Now.AddMinutes(1)));
        Assert.Equal(Now, round.ClosedAt);
    }

    [Fact]
    public void A_rejected_reservation_closes_the_round_without_ever_playing()
    {
        var round = Placed();

        Assert.True(round.MarkRejected("InsufficientFunds", Now));

        Assert.Equal(RoundStatus.Rejected, round.Status);
        Assert.Null(round.WinningNumber);
        Assert.Equal("InsufficientFunds", round.FailureReason);
        Assert.False(round.MarkResolved(1, 1));
    }

    [Fact]
    public void A_round_can_be_voided_before_or_after_the_draw_but_keeps_the_drawn_number_for_audit()
    {
        var beforeDraw = Placed();
        Assert.True(beforeDraw.MarkVoided("ReservationExpired", Now));
        Assert.Null(beforeDraw.WinningNumber);

        var afterDraw = Placed();
        afterDraw.MarkResolved(17, 200);
        Assert.True(afterDraw.MarkVoided("ReservationNotOpen", Now));

        Assert.Equal(RoundStatus.Voided, afterDraw.Status);
        Assert.Equal(17, afterDraw.WinningNumber);
    }

    [Fact]
    public void A_settled_round_can_no_longer_be_voided_or_rejected()
    {
        var round = Placed();
        round.MarkResolved(17, 200);
        round.MarkSettled(Now);

        Assert.False(round.MarkVoided("late", Now));
        Assert.False(round.MarkRejected("late", Now));
        Assert.Equal(RoundStatus.Settled, round.Status);
    }

    [Fact]
    public void Settling_requires_the_draw_to_have_happened()
    {
        var round = Placed();

        Assert.False(round.MarkSettled(Now));
        Assert.Equal(RoundStatus.Placed, round.Status);
    }

    [Fact]
    public void The_stored_bet_can_be_rebuilt_to_pay_the_draw()
    {
        var round = Placed();
        round.BetType = RouletteBetType.Straight;
        round.Selection = [17];

        Assert.Equal(3_600, round.ToBets().Single().PayoutFor(17));
    }

    [Fact]
    public void A_round_saved_with_a_single_bet_in_the_old_format_still_reads_as_one_bet()
    {
        var round = Placed();
        round.BetType = RouletteBetType.Red;
        round.Selection = [];
        round.Stake = 100;
        round.Bets = []; // asi quedaron guardadas las rondas anteriores a las tiradas con varias apuestas

        var bet = Assert.Single(round.AllBets());

        Assert.Equal(RouletteBetType.Red, bet.BetType);
        Assert.Equal(100, bet.Stake);
    }

    [Fact]
    public void A_round_with_several_bets_rebuilds_all_of_them()
    {
        var round = Placed();
        round.Bets =
        [
            new RoundBet { BetType = RouletteBetType.Red, Stake = 100 },
            new RoundBet { BetType = RouletteBetType.Straight, Selection = [7], Stake = 10 },
        ];

        Assert.Equal(2, round.ToBets().Count);
    }
}
