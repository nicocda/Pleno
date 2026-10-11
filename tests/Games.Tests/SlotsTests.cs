using Casino.Modules.Games.Fairness;
using Casino.Modules.Games.Application;
using Casino.Modules.Games.Roulette;
using Casino.Modules.Games.Slots;
using Microsoft.Extensions.Configuration;

namespace Casino.Games.Tests;

public class SlotsPaytableTests
{
    private static SlotSymbol[] Two(long pay) => [new("A", 1, pay), new("B", 1, pay)];

    [Fact]
    public void The_default_table_returns_about_96_percent_with_cascades_and_pays_one_spin_in_three()
    {
        var table = SlotsPaytable.Default;

        Assert.Equal(64, table.TotalWeight);
        Assert.InRange(table.ReturnToPlayerPercent, 96.3, 96.9);
        Assert.InRange(table.HitRatePercent, 33.0, 34.5);
    }

    /// <summary>Calculo de referencia, escrito distinto (recursion sobre cadenas y decimales): debe coincidir con el exacto de la tabla.</summary>
    private static double ReferenceReturn(SlotsPaytable t)
    {
        double Expected(int[] reels, int step)
        {
            var value = (double)t.MultiplierFor(reels) * SlotsPaytable.CascadeMultipliers[step];
            var exploding = t.CascadingReels(reels);
            if (exploding == 0 || step == SlotsPaytable.CascadeMultipliers.Count - 1)
            {
                return value;
            }

            return value + Redraw((int[])reels.Clone(), 0, exploding, 1.0, step + 1);
        }

        double Redraw(int[] reels, int index, int count, double probability, int step)
        {
            if (index == count)
            {
                return probability * Expected((int[])reels.Clone(), step);
            }

            var sum = 0.0;
            for (var symbol = 0; symbol < t.Symbols.Count; symbol++)
            {
                reels[index] = symbol;
                sum += Redraw(reels, index + 1, count, probability * t.Symbols[symbol].Weight / t.TotalWeight, step);
            }

            return sum;
        }

        var total = 0.0;
        for (var a = 0; a < t.Symbols.Count; a++)
        {
            for (var b = 0; b < t.Symbols.Count; b++)
            {
                for (var c = 0; c < t.Symbols.Count; c++)
                {
                    var p = (double)t.Symbols[a].Weight * t.Symbols[b].Weight * t.Symbols[c].Weight / Math.Pow(t.TotalWeight, 3);
                    total += p * Expected([a, b, c], 0);
                }
            }
        }

        return total * 100;
    }

    [Fact]
    public void The_exact_return_matches_an_independent_calculation()
    {
        Assert.Equal(ReferenceReturn(SlotsPaytable.Default), SlotsPaytable.Default.ReturnToPlayerPercent, 3);

        var odd = new SlotsPaytable([new("A", 5, 1), new("B", 2, 2), new("C", 1, 3)], [new LeadingPay("A", 2, 1), new LeadingPay("A", 1, 1), new LeadingPay("B", 2, 2)], 100);
        Assert.Equal(ReferenceReturn(odd), odd.ReturnToPlayerPercent, 3);
    }

    [Fact]
    public void The_return_is_computed_exactly_with_integers()
    {
        // Dos simbolos de igual peso, AAA y BBB pagan x2 y encadenan. En cada paso se gana con probabilidad (2/8) = 1/4,
        // asi que el multiplicador esperado es 2 * (1/4 + 2/16 + 3/64 + 5/256 + 10/1024) = 2 * 462 / 1024.
        var table = new SlotsPaytable(Two(2), [], maxStake: 100);

        Assert.Equal(32_768, table.Denominator); // 2 paradas ^ (3 rodillos * 5 pasos)
        Assert.Equal(2 * 462 * 32, table.ExpectedPayoutUnits);
        Assert.Equal(100.0 * 924 / 1024, table.ReturnToPlayerPercent, 6);
    }

    [Fact]
    public void A_table_that_would_make_the_house_lose_is_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() => new SlotsPaytable(Two(3), [], maxStake: 100)); // 3 * 462 / 1024 > 100%

        Assert.Contains("100%", ex.Message);
    }

    [Fact]
    public void A_table_that_never_pays_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new SlotsPaytable(Two(0), [], maxStake: 100));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    public void Weights_and_symbol_count_are_validated(int weight, int count)
    {
        var symbols = Enumerable.Range(0, count).Select(i => new SlotSymbol($"S{i}", weight, 1));

        Assert.Throws<ArgumentException>(() => new SlotsPaytable(symbols, [], maxStake: 100));
    }

    [Fact]
    public void Repeated_or_blank_symbol_names_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new SlotsPaytable([new("A", 1, 1), new("a", 1, 1)], [], 100));
        Assert.Throws<ArgumentException>(() => new SlotsPaytable([new("A", 1, 1), new(" ", 1, 1)], [], 100));
    }

    [Fact]
    public void Leading_pays_must_refer_to_a_known_symbol_and_a_partial_run()
    {
        SlotSymbol[] symbols = [new("A", 3, 2), new("B", 1, 2)];

        Assert.Throws<ArgumentException>(() => new SlotsPaytable(symbols, [new LeadingPay("Z", 1, 1)], 100));
        Assert.Throws<ArgumentException>(() => new SlotsPaytable(symbols, [new LeadingPay("A", 3, 1)], 100)); // 3 es el pago triple
        Assert.Throws<ArgumentException>(() => new SlotsPaytable(symbols, [new LeadingPay("A", 1, 1), new LeadingPay("a", 1, 2)], 100));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_000_001)]
    public void The_maximum_stake_is_bounded(long max)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlotsPaytable(Two(2), [], max));
    }

    [Fact]
    public void Three_of_a_kind_and_leading_cherries_pay_according_to_the_table()
    {
        var t = SlotsPaytable.Default;
        int Cherry() => t.IndexOf("Cereza");
        int Seven() => t.IndexOf("Siete");
        int Lemon() => t.IndexOf("Limon");

        Assert.Equal(60, t.MultiplierFor([Seven(), Seven(), Seven()]));
        Assert.Equal(4, t.MultiplierFor([Cherry(), Cherry(), Cherry()]));
        Assert.Equal(3, t.MultiplierFor([Cherry(), Cherry(), Lemon()]));
        Assert.Equal(1, t.MultiplierFor([Cherry(), Lemon(), Lemon()]));
        Assert.Equal(1, t.MultiplierFor([Cherry(), Lemon(), Cherry()])); // la cereza del medio o del final no cuenta: solo la racha desde la izquierda
        Assert.Equal(0, t.MultiplierFor([Lemon(), Cherry(), Cherry()]));
        Assert.Equal(0, t.MultiplierFor([Seven(), Seven(), Lemon()]));
    }

    [Fact]
    public void Every_stop_of_the_reel_maps_to_a_symbol_in_proportion_to_its_weight()
    {
        var t = SlotsPaytable.Default;

        var counts = Enumerable.Range(0, t.TotalWeight).GroupBy(t.SymbolAtStop).ToDictionary(g => g.Key, g => g.Count());

        for (var i = 0; i < t.Symbols.Count; i++)
        {
            Assert.Equal(t.Symbols[i].Weight, counts[i]);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => t.SymbolAtStop(t.TotalWeight));
        Assert.Throws<ArgumentOutOfRangeException>(() => t.SymbolAtStop(-1));
    }

    [Fact]
    public void A_malformed_combination_is_refused()
    {
        Assert.Throws<ArgumentException>(() => SlotsPaytable.Default.MultiplierFor([0, 0]));
        Assert.Throws<ArgumentException>(() => SlotsPaytable.Default.MultiplierFor([0, 0, 99]));
    }

    [Fact]
    public void Without_a_Slots_section_the_default_table_is_used()
    {
        var config = new ConfigurationBuilder().Build();

        Assert.Same(SlotsPaytable.Default, SlotsOptions.Load(config));
    }

    [Fact]
    public void The_table_can_be_changed_from_configuration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Slots:MaxStake"] = "500",
            ["Slots:Symbols:0:Name"] = "Rojo",
            ["Slots:Symbols:0:Weight"] = "3",
            ["Slots:Symbols:0:TriplePayout"] = "1",
            ["Slots:Symbols:1:Name"] = "Azul",
            ["Slots:Symbols:1:Weight"] = "1",
            ["Slots:Symbols:1:TriplePayout"] = "3",
        }).Build();

        var table = SlotsOptions.Load(config);

        Assert.Equal(500, table.MaxStake);
        Assert.Equal(["Rojo", "Azul"], table.Symbols.Select(s => s.Name));
        Assert.Equal(ReferenceReturn(table), table.ReturnToPlayerPercent, 3);
        Assert.True(table.ReturnToPlayerPercent > 100.0 * 30 / 64); // sin cascadas serian 30 / 64: el azul (x3) encadena y suma
    }

    [Fact]
    public void Only_prizes_that_beat_the_stake_chain_and_only_the_reels_that_formed_them_spin_again()
    {
        var t = SlotsPaytable.Default;
        int Cherry() => t.IndexOf("Cereza");
        int Seven() => t.IndexOf("Siete");
        int Lemon() => t.IndexOf("Limon");

        Assert.Equal(3, t.CascadingReels([Seven(), Seven(), Seven()]));
        Assert.Equal(2, t.CascadingReels([Cherry(), Cherry(), Lemon()])); // x3: supera la apuesta
        Assert.Equal(0, t.CascadingReels([Cherry(), Lemon(), Lemon()])); // x1: solo recupera la apuesta, no encadena
        Assert.Equal(0, t.CascadingReels([Lemon(), Lemon(), Seven()]));
    }

    [Fact]
    public void The_cascade_multipliers_start_at_one_and_only_go_up()
    {
        Assert.Equal(1, SlotsPaytable.CascadeMultipliers[0]);
        Assert.Equal(SlotsPaytable.CascadeMultipliers.Order(), SlotsPaytable.CascadeMultipliers);
    }

    [Fact]
    public void A_configuration_with_more_than_100_percent_return_does_not_start()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Slots:Symbols:0:Name"] = "A",
            ["Slots:Symbols:0:Weight"] = "1",
            ["Slots:Symbols:0:TriplePayout"] = "50",
            ["Slots:Symbols:1:Name"] = "B",
            ["Slots:Symbols:1:Weight"] = "1",
            ["Slots:Symbols:1:TriplePayout"] = "50",
        }).Build();

        Assert.Throws<ArgumentException>(() => SlotsOptions.Load(config));
    }
}

public class SlotsGameTests
{
    private const string ServerSeed = "9f2c4a7e1b3d58606a1f0e9d8c7b6a5f4e3d2c1b0a99887766554433221100ff";
    private const string ClientSeed = "pleno-lab";
    private static readonly SlotsPaytable Table = SlotsPaytable.Default;

    [Fact]
    public void A_spin_uses_one_number_of_the_fair_stream_per_reel()
    {
        var rng = new FairRng(ServerSeed, ClientSeed, 7);
        var expected = Enumerable.Range(0, 3).Select(_ => Table.Symbols[Table.SymbolAtStop(rng.NextInt(Table.TotalWeight))].Name).ToArray();

        var outcome = SlotsGame.Play(Table, 100, ServerSeed, ClientSeed, 7);

        Assert.Equal(expected, outcome.Reels);
    }

    [Fact]
    public void The_same_inputs_always_give_the_same_spin_and_each_nonce_its_own()
    {
        Assert.Equivalent(SlotsGame.Play(Table, 100, ServerSeed, ClientSeed, 3), SlotsGame.Play(Table, 100, ServerSeed, ClientSeed, 3));

        var different = Enumerable.Range(0, 30).Select(n => string.Join(',', SlotsGame.Play(Table, 100, ServerSeed, ClientSeed, n).Reels)).Distinct().Count();
        Assert.True(different > 5);
    }

    [Fact]
    public void The_payout_is_the_stake_times_the_multiplier_and_always_a_whole_number()
    {
        for (var nonce = 0; nonce < 2_000; nonce++)
        {
            var outcome = SlotsGame.Play(Table, 37, ServerSeed, ClientSeed, nonce);

            Assert.Equal(37 * outcome.Multiplier, outcome.Payout);
            Assert.InRange(outcome.Payout, 0, 37 * 60 * SlotsPaytable.CascadeMultipliers.Sum()); // tope: el mayor premio en cada paso de la cadena
        }
    }

    [Fact]
    public void The_cascades_chain_by_the_rules_and_the_total_is_the_sum_of_the_steps()
    {
        var chained = 0;
        for (var nonce = 0; nonce < 5_000; nonce++)
        {
            var outcome = SlotsGame.Play(Table, 10, ServerSeed, ClientSeed, nonce);
            var steps = outcome.Steps;

            Assert.Equal(steps[0].Reels, outcome.Reels);
            Assert.InRange(steps.Count, 1, SlotsPaytable.CascadeMultipliers.Count);
            Assert.Equal(steps.Sum(s => s.Pay * s.Multiplier), outcome.Multiplier);
            Assert.Equal(SlotsPaytable.CascadeMultipliers.Take(steps.Count), steps.Select(s => s.Multiplier));

            for (var i = 0; i < steps.Count - 1; i++)
            {
                // Se encadena solo si el paso anterior supero la apuesta, y solo giran de nuevo los rodillos que formaron el premio.
                var before = steps[i].Reels.Select(Table.IndexOf).ToArray();
                var exploding = Table.CascadingReels(before);
                Assert.True(exploding > 0);
                Assert.Equal(steps[i].Reels.Skip(exploding), steps[i + 1].Reels.Skip(exploding));
            }

            // La cadena termina porque el ultimo paso no encadena (o porque se agotaron los multiplicadores).
            var last = steps[^1].Reels.Select(Table.IndexOf).ToArray();
            Assert.True(Table.CascadingReels(last) == 0 || steps.Count == SlotsPaytable.CascadeMultipliers.Count);
            if (steps.Count > 1)
            {
                chained++;
            }
        }

        Assert.True(chained > 100, "con la tabla por defecto, mas o menos uno de cada diez giros encadena");
    }

    [Fact]
    public void A_stake_of_zero_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SlotsGame.Play(Table, 0, ServerSeed, ClientSeed, 0));
    }

    [Fact]
    public void Over_many_spins_the_real_return_matches_the_table_and_wins_come_every_now_and_then()
    {
        const int spins = 200_000;
        long staked = 0;
        long paid = 0;
        var wins = 0;
        for (var nonce = 0; nonce < spins; nonce++)
        {
            var outcome = SlotsGame.Play(Table, 10, ServerSeed, ClientSeed, nonce);
            staked += 10;
            paid += outcome.Payout;
            if (outcome.Payout > 0)
            {
                wins++;
            }
        }

        var realReturn = 100.0 * paid / staked;
        var realHitRate = 100.0 * wins / spins;
        Assert.InRange(realReturn, Table.ReturnToPlayerPercent - 4, Table.ReturnToPlayerPercent + 4);
        Assert.InRange(realHitRate, Table.HitRatePercent - 1, Table.HitRatePercent + 1);
        Assert.True(paid < staked * 1.04, "la casa no deberia perder a la larga");
    }
}

public class SlotsSpinTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static SlotsSpin Placed() => new() { Id = Guid.NewGuid(), Status = RoundStatus.Placed, Stake = 100 };

    [Fact]
    public void A_spin_goes_placed_resolved_settled_and_repeating_a_step_changes_nothing()
    {
        var spin = Placed();

        Assert.True(spin.MarkResolved(["Cereza", "Limon", "Bar"], 1, 100));
        Assert.False(spin.MarkResolved(["Siete", "Siete", "Siete"], 100, 10_000)); // entrega duplicada
        Assert.Equal(100, spin.Payout);
        Assert.True(spin.MarkSettled(Now));
        Assert.False(spin.MarkSettled(Now));
        Assert.True(spin.IsClosed);
    }

    [Fact]
    public void A_rejected_spin_never_played_and_cannot_be_settled()
    {
        var spin = Placed();

        Assert.True(spin.MarkRejected("InsufficientFunds", Now));
        Assert.False(spin.MarkResolved(["A", "B", "C"], 0, 0));
        Assert.False(spin.MarkSettled(Now));
        Assert.Equal(RoundStatus.Rejected, spin.Status);
    }

    [Fact]
    public void A_voided_spin_keeps_the_drawn_result_but_cannot_be_settled_afterwards()
    {
        var spin = Placed();
        spin.MarkResolved(["Siete", "Siete", "Siete"], 100, 10_000);

        Assert.True(spin.MarkVoided("ReservationExpired", Now));
        Assert.False(spin.MarkSettled(Now));
        Assert.Equal(3, spin.Reels.Count);
        Assert.Equal(RoundStatus.Voided, spin.Status);
    }
}

public class SlotsCascadeCostTests
{
    [Fact]
    public void The_biggest_allowed_table_is_validated_quickly_because_the_admin_preview_does_it_on_every_edit()
    {
        // 12 simbolos, todos pagando algo que encadena: es el peor caso para el calculo exacto de las cascadas.
        var symbols = Enumerable.Range(0, 12).Select(i => new SlotSymbol($"S{i}", 1_000 + i, 2));
        var leading = Enumerable.Range(0, 12).SelectMany(i => new[] { new LeadingPay($"S{i}", 2, 2), new LeadingPay($"S{i}", 1, 2) });

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var ex = Record.Exception(() => new SlotsPaytable(symbols, leading, 100));
        clock.Stop();

        Assert.IsType<ArgumentException>(ex); // paga mas del 100%: se rechaza, pero despues de calcularlo exacto
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"tardo {clock.Elapsed}");
    }
}
