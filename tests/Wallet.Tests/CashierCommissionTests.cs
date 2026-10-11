using Casino.Modules.Wallet.Application;
using Microsoft.Extensions.Configuration;

namespace Casino.Wallet.Tests;

public class CashierCommissionTests
{
    [Theory]
    [InlineData(1_000, 20, 1_000, 10_000, 20)] // 2 % de 1.000
    [InlineData(999, 20, 1_000, 10_000, 19)] // hacia abajo: 19,98 -> 19
    [InlineData(49, 20, 1_000, 10_000, 0)] // 0,98 -> 0: una carga muy chica no paga
    [InlineData(100_000, 20, 1_000, 10_000, 1_000)] // el tope por carga
    [InlineData(1_000, 20, 1_000, 7, 7)] // lo que le queda del tope diario
    [InlineData(1_000, 20, 1_000, 0, 0)] // tope diario agotado
    [InlineData(1_000, 20, 1_000, -50, 0)] // nunca negativa aunque el diario ya se haya pasado
    [InlineData(1_000, 0, 1_000, 10_000, 0)] // no cobra
    [InlineData(0, 20, 1_000, 10_000, 0)]
    [InlineData(-5, 20, 1_000, 10_000, 0)]
    [InlineData(long.MaxValue, 200, 1_000, 10_000, 1_000)] // sin desbordar
    public void The_commission_is_a_whole_percentage_of_the_load_capped_per_load_and_per_day(long amount, int permille, long perLoad, long remaining, long expected)
    {
        Assert.Equal(expected, CashierCommissionMath.Compute(amount, permille, perLoad, remaining));
    }

    [Fact]
    public void The_defaults_pay_two_percent_to_cashiers_and_nothing_to_heads()
    {
        var options = new CashierOptions();

        Assert.Equal(20, options.PermilleFor(HierarchyLevel.Cashier));
        Assert.Equal(0, options.PermilleFor(HierarchyLevel.HeadCashier));
        Assert.Equal(0, options.PermilleFor(HierarchyLevel.Player));
    }

    [Theory]
    [InlineData("Cashiers:CashierCommissionPermille", "201")]
    [InlineData("Cashiers:HeadCashierCommissionPermille", "-1")]
    [InlineData("Cashiers:MaxCommissionPerLoad", "-1")]
    [InlineData("Cashiers:MaxCommissionPerDay", "-1")]
    public void An_absurd_setting_stops_the_service_instead_of_leaving_a_broken_commission(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([new KeyValuePair<string, string?>(key, value)]).Build();

        Assert.Throws<InvalidOperationException>(() => CashierOptions.Load(configuration));
    }

    [Fact]
    public void Settings_are_read_from_the_Cashiers_section()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
        [
            new KeyValuePair<string, string?>("Cashiers:CashierCommissionPermille", "50"),
            new KeyValuePair<string, string?>("Cashiers:MaxCommissionPerDay", "123"),
        ]).Build();

        var options = CashierOptions.Load(configuration);

        Assert.Equal((50, 123L, 1_000L), (options.CashierCommissionPermille, options.MaxCommissionPerDay, options.MaxCommissionPerLoad));
    }
}
