using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casino.Integration.Tests.Wallet;
using Casino.Modules.Games.Tables;
using Casino.Modules.Wallet.Application;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Casino.Integration.Tests.Games;

/// <summary>
/// Una partida de mesa en curso renueva las reservas de sus asientos cada tanto (<c>Tables:ReservationRenewSeconds</c>) para no perder las fichas por el plazo de la Wallet. Una mesa que todavia
/// espera jugadores no hace falta que renueve (espera menos que el plazo), y una que ya termino tampoco.
/// </summary>
[Collection(WalletDbDefinition.Name)]
public sealed class TableReservationRenewalTests(PostgresFixture db, RabbitMqFixture rabbit) : IDisposable
{
    private const long BuyIn = 100;

    private readonly List<CasinoCluster> _clusters = [];

    public void Dispose()
    {
        foreach (var cluster in _clusters)
        {
            cluster.Dispose();
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (Exception)
            {
                // Todavia no esta listo.
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"No se cumplio a tiempo: {what}");
    }

    [Fact]
    public async Task A_running_table_renews_the_reservation_of_every_seat_and_stops_when_it_is_over()
    {
        var cluster = TestAuth.StartApp(db, rabbit, customize: builder => builder.UseSetting("Tables:ReservationRenewSeconds", "1"));
        _clusters.Add(cluster);
        var wallet = cluster.Wallet.Services.GetRequiredService<WalletService>();
        var tables = cluster.Games.Services.GetRequiredService<TableService>();
        var userOne = Guid.NewGuid();
        var userTwo = Guid.NewGuid();
        var accountOne = await wallet.OpenAccountAsync(userOne);
        var accountTwo = await wallet.OpenAccountAsync(userTwo);
        await wallet.CreditAsync(accountOne, "initial-1", 1_000);
        await wallet.CreditAsync(accountTwo, "initial-2", 1_000);
        using var one = cluster.ClientFor(userOne);
        using var two = cluster.ClientFor(userTwo);

        var request = new HttpRequestMessage(HttpMethod.Post, "/games/poker/tables") { Content = JsonContent.Create(new { buyIn = BuyIn, maxPlayers = 2 }) };
        request.Headers.Add("Idempotency-Key", "renew-1");
        var tableId = (await (await one.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tableId").GetGuid();
        Assert.Equal(HttpStatusCode.Accepted, (await two.PostAsync($"/games/poker/tables/{tableId}/join", content: null)).StatusCode);
        await WaitUntilAsync(async () => (await one.GetFromJsonAsync<JsonElement>($"/games/poker/tables/{tableId}")).GetProperty("seats").EnumerateArray().All(s => s.GetProperty("ready").GetBoolean()), "las fichas reservadas");

        var reservationOne = (await wallet.GetAsync(accountOne)).OpenReservations.Keys.Single();
        var reservationTwo = (await wallet.GetAsync(accountTwo)).OpenReservations.Keys.Single();
        Assert.Null(await wallet.GetRenewedExpiryAsync(reservationOne)); // todavia nadie renovo: la mesa espera jugadores

        // Mientras la mesa espera sin empezar, el motor no renueva nada.
        await tables.TickAsync();
        await Task.Delay(1_500);
        await tables.TickAsync();
        Assert.Null(await wallet.GetRenewedExpiryAsync(reservationOne));

        Assert.Equal(HttpStatusCode.NoContent, (await one.PostAsync($"/games/poker/tables/{tableId}/start", content: null)).StatusCode);

        // En juego: pasado el intervalo, el motor renueva a todos y la Wallet guarda el plazo nuevo (la hora que dura la reserva, a partir de ahora).
        await WaitUntilAsync(async () =>
        {
            await tables.TickAsync();
            return await wallet.GetRenewedExpiryAsync(reservationOne) is not null && await wallet.GetRenewedExpiryAsync(reservationTwo) is not null;
        }, "las reservas renovadas", seconds: 30);
        var renewedAt = await wallet.GetRenewedExpiryAsync(reservationOne);
        Assert.True(renewedAt > DateTimeOffset.UtcNow.AddMinutes(55), "el plazo nuevo es de una hora desde la renovacion");

        // Cada tanto vuelve a renovar: el plazo avanza.
        await WaitUntilAsync(async () =>
        {
            await tables.TickAsync();
            return await wallet.GetRenewedExpiryAsync(reservationOne) > renewedAt;
        }, "otra renovacion", seconds: 30);

        // Las fichas siguen reservadas (nada se libero) y, al cancelarse la mesa, se devuelven normalmente.
        Assert.Equal(BuyIn + (BuyIn / 10), (await wallet.GetAsync(accountOne)).Reserved); // la entrada mas la comision (10 %)
        await tables.CancelAsync(tableId, "test");
        await WaitUntilAsync(async () => (await wallet.GetAsync(accountOne)) is { Reserved: 0, Available: 1_000 }, "las fichas devueltas al cancelar");
    }
}
