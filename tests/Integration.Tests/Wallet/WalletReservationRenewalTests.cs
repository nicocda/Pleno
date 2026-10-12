using Casino.Contracts;
using Casino.Modules.Wallet.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace Casino.Integration.Tests.Wallet;

/// <summary>
/// Renovar una reserva: una partida larga (una mesa de poker) renueva el plazo de las fichas que reservo para no perderlas mientras sigue jugando. Si deja de renovar, la reserva vence sola
/// como siempre. El vencimiento que ya estaba programado, al llegar, ve que la reserva fue renovada y no la libera.
/// </summary>
[Collection(WalletDbDefinition.Name)]
public sealed class WalletReservationRenewalTests(PostgresFixture db, RabbitMqFixture rabbit) : IDisposable
{
    private readonly CasinoCluster _cluster = TestAuth.StartApp(db, rabbit);

    public void Dispose() => _cluster.Dispose();

    private WalletService Wallet => _cluster.Wallet.Services.GetRequiredService<WalletService>();

    private IMessageBus Bus => _cluster.Games.Services.GetRequiredService<IHost>().MessageBus();

    private async Task<Guid> FundedAccountAsync(long chips)
    {
        var accountId = await Wallet.OpenAccountAsync(Guid.NewGuid());
        await Wallet.CreditAsync(accountId, "initial-credit", chips);
        return accountId;
    }

    private async Task<Guid> ReserveAsync(Guid accountId, int ttlSeconds)
    {
        var bet = Guid.NewGuid();
        await Bus.PublishAsync(new ReserveStake(bet, accountId, 100, ttlSeconds));
        await WaitUntilAsync(async () => (await Wallet.GetAsync(accountId)).Reserved == 100, "la reserva hecha");
        return bet;
    }

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
    public async Task A_renewed_reservation_survives_its_original_term_and_expires_at_the_new_one()
    {
        var accountId = await FundedAccountAsync(1_000);
        var bet = await ReserveAsync(accountId, ttlSeconds: 2);

        await Bus.PublishAsync(new ExtendReservation(bet, accountId, TtlSeconds: 8));
        await WaitUntilAsync(async () => await Wallet.GetRenewedExpiryAsync(bet) is not null, "la renovacion");

        await Task.Delay(4_000); // paso el plazo original (2 s): el vencimiento viejo llego y no la libero
        Assert.Equal((900L, 100L), ((await Wallet.GetAsync(accountId)).Available, (await Wallet.GetAsync(accountId)).Reserved));

        // Si nadie la renueva mas, vence en el plazo nuevo.
        await WaitUntilAsync(async () => (await Wallet.GetAsync(accountId)) is { Reserved: 0, Available: 1_000 }, "la reserva liberada al nuevo plazo", seconds: 25);
        await WaitUntilAsync(async () => await Wallet.GetRenewedExpiryAsync(bet) is null, "el registro de la renovacion limpiado");
    }

    [Fact]
    public async Task A_reservation_can_be_renewed_again_and_again_while_the_game_keeps_asking()
    {
        var accountId = await FundedAccountAsync(1_000);
        var bet = await ReserveAsync(accountId, ttlSeconds: 2);

        for (var i = 0; i < 4; i++)
        {
            await Bus.PublishAsync(new ExtendReservation(bet, accountId, TtlSeconds: 3));
            await Task.Delay(1_500); // cada renovacion llega antes de que venza el plazo anterior
        }

        Assert.Equal(100L, (await Wallet.GetAsync(accountId)).Reserved); // pasaron 6 s: el plazo original (2 s) vencio hace rato
        await WaitUntilAsync(async () => (await Wallet.GetAsync(accountId)).Reserved == 0, "la reserva liberada al dejar de renovar", seconds: 20);
    }

    [Fact]
    public async Task A_renewed_reservation_still_settles_normally_and_nothing_is_released_afterwards()
    {
        var accountId = await FundedAccountAsync(1_000);
        var bet = await ReserveAsync(accountId, ttlSeconds: 2);
        await Bus.PublishAsync(new ExtendReservation(bet, accountId, TtlSeconds: 4));
        await WaitUntilAsync(async () => await Wallet.GetRenewedExpiryAsync(bet) is not null, "la renovacion");

        await Wallet.SettleAsync(accountId, "settle-renewed", bet, payout: 250);

        var after = await Wallet.GetAsync(accountId);
        Assert.Equal((1_150L, 0L), (after.Available, after.Reserved)); // 1.000 - 100 + 250
        await Task.Delay(6_000); // llegan los vencimientos viejos: la reserva ya esta cerrada, no pasa nada
        Assert.Equal((1_150L, 0L), ((await Wallet.GetAsync(accountId)).Available, (await Wallet.GetAsync(accountId)).Reserved));
        await WaitUntilAsync(async () => await Wallet.GetRenewedExpiryAsync(bet) is null, "el registro de la renovacion limpiado");
    }

    [Fact]
    public async Task Renewing_something_that_is_not_open_does_nothing_and_a_renewal_never_shortens_the_term()
    {
        var accountId = await FundedAccountAsync(1_000);

        Assert.False(await Wallet.ExtendReservationAsync(accountId, Guid.NewGuid(), TimeSpan.FromSeconds(30))); // nunca existio

        var bet = await ReserveAsync(accountId, ttlSeconds: 3_600);
        Assert.True(await Wallet.ExtendReservationAsync(accountId, bet, TimeSpan.FromSeconds(60)));
        var longer = await Wallet.GetRenewedExpiryAsync(bet);
        Assert.True(await Wallet.ExtendReservationAsync(accountId, bet, TimeSpan.FromSeconds(1))); // pide menos: se queda con el plazo mas largo
        Assert.Equal(longer, await Wallet.GetRenewedExpiryAsync(bet));

        await Wallet.SettleAsync(accountId, "settle-closed", bet, payout: 0);
        Assert.False(await Wallet.ExtendReservationAsync(accountId, bet, TimeSpan.FromSeconds(30))); // ya se cerro
        Assert.Equal((900L, 0L), ((await Wallet.GetAsync(accountId)).Available, (await Wallet.GetAsync(accountId)).Reserved));
    }
}
