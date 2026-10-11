using Casino.Modules.Games.Application;
using Casino.Modules.Games.Crash;
using Casino.Modules.Games.Fairness;

namespace Casino.Games.Tests;

public class CrashMathTests
{
    private const string Seed = "9f2c4a7e1b3d58606a1f0e9d8c7b6a5f4e3d2c1b0a99887766554433221100ff";

    private static Guid Round(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:x12}");

    // Vectores calculados con una implementacion INDEPENDIENTE (Python: hmac + enteros), para contrastar la del servidor.
    // Los mismos vectores los usa la verificacion del navegador (web/src/lib/crash.test.ts).
    [Theory]
    [InlineData(1, 30, 346)]
    [InlineData(2, 30, 1198)]
    [InlineData(3, 30, 143)]
    [InlineData(4, 30, 114)]
    [InlineData(5, 30, 176)]
    [InlineData(6, 30, 238)]
    [InlineData(7, 30, 100)] // explosion instantanea
    [InlineData(8, 30, 395)]
    [InlineData(1, 0, 357)]
    [InlineData(2, 0, 1235)]
    [InlineData(1, 100, 321)]
    [InlineData(7, 100, 100)]
    public void The_crash_point_matches_the_reference_vectors(int round, int edge, long expected)
    {
        Assert.Equal(expected, CrashMath.CrashPoint(Seed, Round(round), edge));
    }

    [Fact]
    public void The_same_seed_and_round_always_give_the_same_point_and_another_round_gives_its_own()
    {
        Assert.Equal(CrashMath.CrashPoint(Seed, Round(1)), CrashMath.CrashPoint(Seed, Round(1)));
        Assert.NotEqual(CrashMath.CrashPoint(Seed, Round(1)), CrashMath.CrashPoint(Seed, Round(2)));
        Assert.NotEqual(CrashMath.CrashPoint(Seed, Round(1)), CrashMath.CrashPoint("otra-semilla", Round(1)));
    }

    [Fact]
    public void A_higher_house_edge_never_raises_the_point()
    {
        for (var i = 1; i <= 200; i++)
        {
            Assert.True(CrashMath.CrashPoint(Seed, Round(i), 100) <= CrashMath.CrashPoint(Seed, Round(i), 30));
            Assert.True(CrashMath.CrashPoint(Seed, Round(i), 30) <= CrashMath.CrashPoint(Seed, Round(i), 0));
        }
    }

    [Fact]
    public void The_point_is_never_below_x1_and_never_above_the_cap()
    {
        for (var i = 1; i <= 5_000; i++)
        {
            var point = CrashMath.CrashPoint(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(i))), Round(i), 0);

            Assert.InRange(point, CrashMath.OneX, CrashMath.MaxMultiplier);
        }
    }

    [Fact]
    public void Over_many_rounds_the_return_for_a_player_who_always_cashes_out_at_the_same_multiplier_is_one_minus_the_edge()
    {
        const int rounds = 200_000;
        var targets = new long[] { 150, 200, 500 };
        var wins = new long[targets.Length];
        var instant = 0;
        var capped = 0;
        for (var i = 0; i < rounds; i++)
        {
            var seed = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(i)));
            var point = CrashMath.CrashPoint(seed, Round(i + 1));
            instant += point == CrashMath.OneX ? 1 : 0;
            capped += point == CrashMath.MaxMultiplier ? 1 : 0;
            for (var t = 0; t < targets.Length; t++)
            {
                wins[t] += point > targets[t] ? 1 : 0; // se retira en `target` y gana solo si la ronda sigue viva (explota despues)
            }
        }

        for (var t = 0; t < targets.Length; t++)
        {
            // Retorno por ficha apostada = P(ganar) x multiplicador. Debe rondar 97 % para cualquier objetivo (la ventaja es del 3 %).
            var returnRate = (double)wins[t] / rounds * targets[t] / 100.0;
            Assert.InRange(returnRate, 0.93, 1.01);
        }

        Assert.InRange((double)instant / rounds, 0.025, 0.055); // ~ la ventaja (3 %) mas las explosiones entre x1,00 y x1,01
        Assert.True(capped > 0 && capped < rounds / 50, "el tope de x100 se alcanza en ~1 de cada 100 rondas");
    }

    [Fact]
    public void The_multiplier_starts_at_x1_only_grows_and_stops_at_the_cap()
    {
        Assert.Equal(100, CrashMath.MultiplierAt(0));
        Assert.Equal(100, CrashMath.MultiplierAt(-5));
        long previous = 100;
        for (var ms = 0; ms <= 130_000; ms += 250)
        {
            var current = CrashMath.MultiplierAt(ms / 1000.0);
            Assert.True(current >= previous);
            Assert.InRange(current, 100, CrashMath.MaxMultiplier);
            previous = current;
        }

        Assert.Equal(CrashMath.MaxMultiplier, CrashMath.MultiplierAt(200));
        Assert.InRange(CrashMath.MultiplierAt(10), 195, 205); // a los 10 s va por ~x2
    }

    [Theory]
    [InlineData(101)]
    [InlineData(150)]
    [InlineData(250)]
    [InlineData(1_000)]
    [InlineData(5_000)]
    [InlineData(10_000)]
    public void The_time_to_reach_a_multiplier_is_the_exact_moment_it_gets_there(long multiplier)
    {
        var moment = CrashMath.TimeToReach(multiplier).TotalSeconds;

        Assert.True(CrashMath.MultiplierAt(moment + 0.002) >= multiplier);
        Assert.True(CrashMath.MultiplierAt(moment - 0.002) < multiplier);
        Assert.Equal(TimeSpan.Zero, CrashMath.TimeToReach(CrashMath.OneX));
    }

    [Fact]
    public void The_growth_speed_can_be_changed()
    {
        Assert.True(CrashMath.MultiplierAt(5, 0.5) > CrashMath.MultiplierAt(5, 0.07));
        Assert.True(CrashMath.TimeToReach(500, 0.5) < CrashMath.TimeToReach(500, 0.07));
    }

    [Theory]
    [InlineData(100, 100, 100)]
    [InlineData(100, 250, 250)]
    [InlineData(7, 150, 10)] // 10,5 -> piso
    [InlineData(1, 199, 1)]
    [InlineData(1, 10_000, 100)]
    public void The_payout_is_always_a_whole_number_of_chips_rounded_down(long stake, long multiplier, long expected)
    {
        Assert.Equal(expected, CrashMath.PayoutFor(stake, multiplier));
    }

    [Fact]
    public void Invalid_inputs_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CrashMath.PayoutFor(0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => CrashMath.PayoutFor(10, 99));
        Assert.Throws<ArgumentOutOfRangeException>(() => CrashMath.CrashPoint(Seed, Round(1), -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CrashMath.CrashPoint(Seed, Round(1), 201));
        Assert.Throws<ArgumentException>(() => CrashMath.CrashPoint(" ", Round(1)));
    }

    [Fact]
    public void The_commitment_published_before_betting_matches_the_seed_revealed_after()
    {
        var commitment = ProvablyFair.Commitment(Seed);

        Assert.Equal("52ca56a3d81d3be381d594a5bca342bb63f6a3597776fed89138c9244fcbd2c8", commitment);
        Assert.True(ProvablyFair.MatchesCommitment(Seed, commitment));
        Assert.False(ProvablyFair.MatchesCommitment(Seed + "0", commitment));
    }
}

public class CrashOptionsTests
{
    [Fact]
    public void The_defaults_are_valid()
    {
        new CrashOptions().Validate();
    }

    [Theory]
    [InlineData(0.01, 4, 0.07, 30, 1, 100)]
    [InlineData(8, -1, 0.07, 30, 1, 100)]
    [InlineData(8, 4, 0, 30, 1, 100)]
    [InlineData(8, 4, 0.07, 500, 1, 100)]
    [InlineData(8, 4, 0.07, 30, 0, 100)]
    [InlineData(8, 4, 0.07, 30, 50, 10)]
    [InlineData(8, 4, 0.07, 30, 1, 10_000_000)]
    public void An_absurd_configuration_stops_the_startup(double betting, double pause, double growth, int edge, long min, long max)
    {
        var options = new CrashOptions { BettingSeconds = betting, PauseSeconds = pause, GrowthPerSecond = growth, EdgePermille = edge, MinStake = min, MaxStake = max };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}

public class CrashBetTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static CrashBet Placed(long stake = 100) => new() { Id = Guid.NewGuid(), Stake = stake, Status = RoundStatus.Placed };

    private static CrashBet Active(long stake = 100)
    {
        var bet = Placed(stake);
        bet.MarkReserved();
        return bet;
    }

    [Fact]
    public void A_bet_is_in_play_only_after_the_wallet_reserved_the_chips()
    {
        var bet = Placed();
        Assert.False(bet.IsActive);

        Assert.True(bet.MarkReserved());
        Assert.False(bet.MarkReserved()); // entrega repetida
        Assert.True(bet.IsActive);
    }

    [Fact]
    public void Cashing_out_pays_the_stake_times_the_multiplier_and_can_happen_only_once()
    {
        var bet = Active(100);

        Assert.True(bet.CashOut(250, 250));
        Assert.False(bet.CashOut(300, 300));
        Assert.Equal((250L, 250L, RoundStatus.Resolved), (bet.CashedOutAt, bet.Payout, bet.Status));
        Assert.False(bet.Lose()); // ya retiro: no se pierde despues
    }

    [Fact]
    public void A_bet_that_was_never_reserved_cannot_cash_out_or_lose()
    {
        var bet = Placed();

        Assert.False(bet.CashOut(200, 200));
        Assert.False(bet.Lose());
    }

    [Fact]
    public void A_bet_that_did_not_cash_out_when_the_rocket_crashed_loses_what_it_staked()
    {
        var bet = Active(100);

        Assert.True(bet.Lose());
        Assert.False(bet.Lose());
        Assert.Equal(0, bet.Payout);
        Assert.False(bet.CashOut(150, 150)); // tarde
    }

    [Fact]
    public void A_late_or_cut_short_bet_gets_exactly_its_stake_back()
    {
        var bet = Placed(70);

        Assert.True(bet.Refund("BettingClosed"));
        Assert.False(bet.Refund("BettingClosed"));
        Assert.Equal((70L, "BettingClosed", RoundStatus.Resolved), (bet.Payout, bet.FailureReason, bet.Status));
    }

    [Fact]
    public void Settling_closes_a_resolved_bet_once_and_a_rejected_one_never_played()
    {
        var won = Active();
        won.CashOut(200, 200);
        Assert.True(won.MarkSettled(Now));
        Assert.False(won.MarkSettled(Now));
        Assert.True(won.IsClosed);

        var broke = Placed();
        Assert.True(broke.MarkRejected("InsufficientFunds", Now));
        Assert.False(broke.MarkSettled(Now));
        Assert.Equal(RoundStatus.Rejected, broke.Status);

        var reserved = Active();
        Assert.False(reserved.MarkRejected("x", Now)); // una apuesta ya reservada no se rechaza
    }

    [Fact]
    public void A_bet_can_be_voided_before_or_after_the_result_but_not_once_closed()
    {
        var open = Active();
        var resolved = Active();
        resolved.Lose();
        var settled = Active();
        settled.Lose();
        settled.MarkSettled(Now);

        Assert.True(open.MarkVoided("ReservationExpired", Now));
        Assert.True(resolved.MarkVoided("StakeSettlementRejected", Now));
        Assert.False(settled.MarkVoided("x", Now));
    }
}

public class CrashRoundTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_round_goes_betting_running_crashed_and_each_step_happens_once()
    {
        var round = new CrashRound { Phase = CrashPhase.Betting };

        Assert.False(round.MarkCrashed(Now, 200, "seed")); // no se puede explotar sin haber empezado
        Assert.True(round.StartRunning(Now));
        Assert.False(round.StartRunning(Now));
        Assert.True(round.MarkCrashed(Now.AddSeconds(5), 200, "seed"));
        Assert.False(round.MarkCrashed(Now.AddSeconds(6), 300, "otra"));
        Assert.Equal((200L, "seed", true), (round.CrashPoint, round.ServerSeed, round.IsFinal));
    }

    [Fact]
    public void A_round_cut_short_is_aborted_unless_it_already_finished()
    {
        var running = new CrashRound { Phase = CrashPhase.Running };
        var done = new CrashRound { Phase = CrashPhase.Crashed };

        Assert.True(running.MarkAborted(Now));
        Assert.False(running.MarkAborted(Now));
        Assert.False(done.MarkAborted(Now));
        Assert.Equal(CrashPhase.Crashed, done.Phase);
    }
}
