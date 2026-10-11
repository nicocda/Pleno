using Casino.Contracts;
using Casino.Modules.Wallet.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace Casino.Integration.Tests.Wallet;

/// <summary>
/// Cuanto dura una reserva de la Wallet. Por defecto vence a los 60 s; un juego de ronda larga (Crash) puede pedir otro plazo en la orden
/// <see cref="ReserveStake"/>, acotado a un rango razonable. La reserva que nadie liquida se libera sola.
/// </summary>
[Collection(WalletDbDefinition.Name)]
public sealed class WalletReservationTtlTests(PostgresFixture db, RabbitMqFixture rabbit) : IDisposable
{
    private readonly CasinoCluster _cluster = TestAuth.StartApp(db, rabbit);

    public void Dispose() => _cluster.Dispose();

    private WalletService Wallet => _cluster.Wallet.Services.GetRequiredService<WalletService>();

    private async Task<Guid> FundedAccountAsync(long chips)
    {
        var accountId = await Wallet.OpenAccountAsync(Guid.NewGuid());
        await Wallet.CreditAsync(accountId, "initial-credit", chips);
        return accountId;
    }

    private async Task ReserveAsync(Guid accountId, Guid bet, int? ttlSeconds) =>
        await _cluster.Games.Services.GetRequiredService<IHost>().MessageBus().PublishAsync(new ReserveStake(bet, accountId, 100, ttlSeconds));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"No se cumplio a tiempo: {what}");
    }

    [Fact]
    public async Task A_game_can_ask_for_a_shorter_reservation_and_the_wallet_releases_it_on_its_own_when_nobody_settles_it()
    {
        var accountId = await FundedAccountAsync(1_000);
        var bet = Guid.NewGuid();

        await ReserveAsync(accountId, bet, ttlSeconds: 1);
        await WaitUntilAsync(async () => (await Wallet.GetAsync(accountId)).Reserved == 100, "la reserva hecha");
        Assert.Equal(1_000L - 100, (await Wallet.GetAsync(accountId)).Available);

        // El plazo por defecto es de 60 s: que se libere en segundos prueba que se uso el pedido.
        await WaitUntilAsync(async () => (await Wallet.GetAsync(accountId)) is { Reserved: 0, Available: 1_000 }, "la reserva liberada por vencimiento", seconds: 20);
    }

    [Fact]
    public async Task Without_a_requested_term_the_default_one_applies_and_the_reservation_stays_open()
    {
        var accountId = await FundedAccountAsync(1_000);

        await ReserveAsync(accountId, Guid.NewGuid(), ttlSeconds: null);
        await WaitUntilAsync(async () => (await Wallet.GetAsync(accountId)).Reserved == 100, "la reserva hecha");
        await Task.Delay(4_000);

        Assert.Equal((900L, 100L), ((await Wallet.GetAsync(accountId)).Available, (await Wallet.GetAsync(accountId)).Reserved));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public async Task A_zero_or_negative_term_is_raised_to_the_minimum_instead_of_expiring_at_once_or_never(int requested)
    {
        var accountId = await FundedAccountAsync(1_000);

        await ReserveAsync(accountId, Guid.NewGuid(), requested);

        await WaitUntilAsync(async () => (await Wallet.GetAsync(accountId)).Reserved == 100, "la reserva hecha");
        await WaitUntilAsync(async () => (await Wallet.GetAsync(accountId)).Reserved == 0, "la reserva liberada al minimo", seconds: 20);
        Assert.Equal(1_000L, (await Wallet.GetAsync(accountId)).Available);
    }

    [Fact]
    public async Task The_wallet_service_applies_the_term_given_to_a_reservation_and_the_limits_are_sane()
    {
        var accountId = await FundedAccountAsync(1_000);
        var bet = Guid.NewGuid();

        await Wallet.ReserveAsync(accountId, "ttl-direct", bet, 100, ttl: TimeSpan.FromSeconds(1));

        await WaitUntilAsync(async () => (await Wallet.GetAsync(accountId)).Reserved == 0, "la reserva liberada por su plazo propio", seconds: 20);
        Assert.Equal(1, ReserveStakeHandler.MinTtlSeconds);
        Assert.InRange(ReserveStakeHandler.MaxTtlSeconds, 120, 3_600); // alcanza para la ronda mas larga de Crash (~100 s) sin ser eterna
    }
}
