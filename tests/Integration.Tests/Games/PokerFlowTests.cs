using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casino.Integration.Tests.Wallet;
using Casino.Modules.Games.Tables;
using Casino.Modules.Wallet.Application;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Casino.Integration.Tests.Games;

/// <summary>
/// Poker (Texas Hold'em) sobre la plataforma de mesas, de punta a punta: dos personas juegan varias manos (con una pausa automatica entre manos) hasta que una se queda con todas las fichas y el
/// pago cierra contra la Wallet. El poker no tiene bots.
/// </summary>
[Collection(WalletDbDefinition.Name)]
public sealed class PokerFlowTests(PostgresFixture db, RabbitMqFixture rabbit) : IDisposable
{
    private const long BuyIn = 100;
    private const long Initial = 1_000;

    private readonly List<CasinoCluster> _clusters = [];

    public void Dispose()
    {
        foreach (var cluster in _clusters)
        {
            cluster.Dispose();
        }
    }

    private static async Task<JsonElement> ViewAsync(HttpClient client, Guid tableId) => await client.GetFromJsonAsync<JsonElement>($"/games/poker/tables/{tableId}");

    private static async Task<(long Available, long Reserved)> BalanceAsync(CasinoCluster cluster, Guid accountId)
    {
        var account = await cluster.Wallet.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        return (account.Available, account.Reserved);
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

    private static Task<HttpResponseMessage> ActAsync(HttpClient client, Guid tableId, string json) =>
        client.PostAsync($"/games/poker/tables/{tableId}/action", new StringContent(json, Encoding.UTF8, "application/json"));

    /// <summary>Le toca a quien mira esta vista, en una mano en juego (no en la pausa entre manos).</summary>
    private static bool MyTurnToPlay(JsonElement view) =>
        view.GetProperty("status").GetString() == "Playing"
        && !view.GetProperty("game").GetProperty("done").GetBoolean()
        && view.GetProperty("turnSeat").ValueKind == JsonValueKind.Number
        && view.GetProperty("turnSeat").GetInt32() == view.GetProperty("mySeat").GetInt32();

    private async Task<(CasinoCluster Cluster, HttpClient One, HttpClient Two, Guid AccountOne, Guid AccountTwo, Guid TableId)> StartTableAsync()
    {
        var cluster = TestAuth.StartApp(db, rabbit);
        _clusters.Add(cluster);
        var wallet = cluster.Wallet.Services.GetRequiredService<WalletService>();
        var userOne = Guid.NewGuid();
        var userTwo = Guid.NewGuid();
        var accountOne = await wallet.OpenAccountAsync(userOne);
        var accountTwo = await wallet.OpenAccountAsync(userTwo);
        await wallet.CreditAsync(accountOne, "initial-credit-1", Initial);
        await wallet.CreditAsync(accountTwo, "initial-credit-2", Initial);
        var one = cluster.ClientFor(userOne);
        var two = cluster.ClientFor(userTwo);

        var rules = await one.GetFromJsonAsync<JsonElement>("/games/poker/rules");
        Assert.Equal((2, 6), (rules.GetProperty("minPlayers").GetInt32(), rules.GetProperty("maxPlayers").GetInt32()));
        Assert.False(rules.GetProperty("allowsBots").GetBoolean());

        var request = new HttpRequestMessage(HttpMethod.Post, "/games/poker/tables") { Content = JsonContent.Create(new { buyIn = BuyIn, maxPlayers = 2 }) };
        request.Headers.Add("Idempotency-Key", "poker-1");
        var tableId = (await (await one.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tableId").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await one.PostAsync($"/games/poker/tables/{tableId}/bots", content: null)).StatusCode); // sin bots
        Assert.Equal(HttpStatusCode.Accepted, (await two.PostAsync($"/games/poker/tables/{tableId}/join", content: null)).StatusCode);
        await WaitUntilAsync(async () => (await ViewAsync(one, tableId)).GetProperty("seats").EnumerateArray().All(s => s.GetProperty("ready").GetBoolean()), "las fichas reservadas");
        Assert.Equal(HttpStatusCode.NoContent, (await one.PostAsync($"/games/poker/tables/{tableId}/start", content: null)).StatusCode);
        return (cluster, one, two, accountOne, accountTwo, tableId);
    }

    [Fact]
    public async Task Two_players_play_several_hands_with_a_pause_in_between_until_one_has_all_the_chips_and_the_balance_closes()
    {
        var (cluster, one, two, accountOne, accountTwo, tableId) = await StartTableAsync();
        var tables = cluster.Games.Services.GetRequiredService<TableService>();
        HttpClient[] clients = [one, two];

        // Mano 1: el que tiene el turno se retira. Nadie se queda sin fichas: la partida sigue.
        await WaitUntilAsync(async () =>
        {
            foreach (var client in clients)
            {
                if (MyTurnToPlay(await ViewAsync(client, tableId)))
                {
                    Assert.Equal(HttpStatusCode.NoContent, (await ActAsync(client, tableId, """{"type":"fold"}""")).StatusCode);
                    return true;
                }
            }

            return false;
        }, "la primera jugada");

        var afterFold = await ViewAsync(one, tableId);
        Assert.Equal("Playing", afterFold.GetProperty("status").GetString());
        Assert.True(afterFold.GetProperty("game").GetProperty("done").GetBoolean()); // la mano termino...
        Assert.False(afterFold.GetProperty("game").GetProperty("over").GetBoolean()); // ...pero la partida no
        Assert.NotEqual(JsonValueKind.Null, afterFold.GetProperty("turnEndsAt").ValueKind); // hay cuenta atras para la mano siguiente

        // La pausa vence sola (el motor juega el turno automatico) y arranca la mano 2.
        await WaitUntilAsync(async () =>
        {
            await tables.TickAsync();
            return (await ViewAsync(one, tableId)).GetProperty("game").GetProperty("handNo").GetInt32() == 2;
        }, "la mano siguiente", seconds: 30);

        // Mano 2 en adelante: quien juega va all-in si puede y el otro iguala, hasta que uno se queda con todo (si empatan, sigue otra mano).
        var deadline = DateTime.UtcNow.AddSeconds(120);
        JsonElement view;
        while (true)
        {
            Assert.True(DateTime.UtcNow < deadline, "La partida no termino a tiempo.");
            view = await ViewAsync(one, tableId);
            if (view.GetProperty("status").GetString() == "Finished")
            {
                break;
            }

            var acted = false;
            foreach (var client in clients)
            {
                var mine = await ViewAsync(client, tableId);
                if (MyTurnToPlay(mine))
                {
                    var game = mine.GetProperty("game");
                    var actions = game.GetProperty("actions").EnumerateArray().Select(a => a.GetString()).ToList();
                    var json = actions.Contains("raise")
                        ? $$"""{"type":"raise","to":{{game.GetProperty("maxRaiseTo").GetInt64()}}}"""
                        : actions.Contains("check") ? """{"type":"check"}""" : """{"type":"call"}""";
                    Assert.Equal(HttpStatusCode.NoContent, (await ActAsync(client, tableId, json)).StatusCode);
                    acted = true;
                    break;
                }
            }

            if (!acted)
            {
                await tables.TickAsync();
                await Task.Delay(50);
            }
        }

        var payouts = view.GetProperty("payouts").EnumerateArray().Select(p => p.GetInt64()).ToList();
        Assert.Equal(2 * BuyIn, payouts.Sum()); // lo que se puso entre los dos
        Assert.Equal(1, payouts.Count(p => p > 0)); // uno se lleva todo
        Assert.True(view.GetProperty("game").GetProperty("over").GetBoolean());

        var seatOne = (await ViewAsync(one, tableId)).GetProperty("mySeat").GetInt32();
        var seatTwo = (await ViewAsync(two, tableId)).GetProperty("mySeat").GetInt32();
        await WaitUntilAsync(async () => await BalanceAsync(cluster, accountOne) == (Initial - BuyIn + payouts[seatOne], 0), "el pago del primero");
        await WaitUntilAsync(async () => await BalanceAsync(cluster, accountTwo) == (Initial - BuyIn + payouts[seatTwo], 0), "el pago del segundo");
    }
}
