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

/// <summary>Poker (Texas Hold'em) sobre la plataforma de mesas, de punta a punta: una persona y dos bots juegan una mano completa y el pago cierra contra la Wallet.</summary>
[Collection(WalletDbDefinition.Name)]
public sealed class PokerFlowTests(PostgresFixture db, RabbitMqFixture rabbit) : IDisposable
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

    /// <summary>La jugada de un jugador simple: pasa si es gratis, si no iguala.</summary>
    private static string? NextAction(JsonElement view)
    {
        if (view.GetProperty("turnSeat").ValueKind != JsonValueKind.Number || view.GetProperty("turnSeat").GetInt32() != view.GetProperty("mySeat").GetInt32())
        {
            return null;
        }

        var actions = view.GetProperty("game").GetProperty("actions").EnumerateArray().Select(a => a.GetString()).ToList();
        return actions.Contains("check") ? """{"type":"check"}""" : """{"type":"call"}""";
    }

    [Fact]
    public async Task A_player_and_two_bots_play_a_full_hand_and_the_balance_closes()
    {
        var cluster = TestAuth.StartApp(db, rabbit, customize: builder => builder.UseSetting("Tables:BotThinkMilliseconds", "0"));
        _clusters.Add(cluster);
        var userId = Guid.NewGuid();
        var wallet = cluster.Wallet.Services.GetRequiredService<WalletService>();
        var accountId = await wallet.OpenAccountAsync(userId);
        await wallet.CreditAsync(accountId, "initial-credit", 1_000);
        using var client = cluster.ClientFor(userId);
        var tables = cluster.Games.Services.GetRequiredService<TableService>();

        var rules = await client.GetFromJsonAsync<JsonElement>("/games/poker/rules");
        Assert.Equal((2, 6), (rules.GetProperty("minPlayers").GetInt32(), rules.GetProperty("maxPlayers").GetInt32()));
        var request = new HttpRequestMessage(HttpMethod.Post, "/games/poker/tables") { Content = JsonContent.Create(new { buyIn = BuyIn, maxPlayers = 3 }) };
        request.Headers.Add("Idempotency-Key", "poker-1");
        var tableId = (await (await client.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tableId").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/games/poker/tables/{tableId}/bots", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/games/poker/tables/{tableId}/bots", content: null)).StatusCode);
        await WaitUntilAsync(async () => (await ViewAsync(client, tableId)).GetProperty("seats").EnumerateArray().All(s => s.GetProperty("ready").GetBoolean()), "las fichas reservadas");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/games/poker/tables/{tableId}/start", content: null)).StatusCode);

        var deadline = DateTime.UtcNow.AddSeconds(120);
        JsonElement view;
        while (true)
        {
            Assert.True(DateTime.UtcNow < deadline, "La partida no termino a tiempo.");
            view = await ViewAsync(client, tableId);
            if (view.GetProperty("status").GetString() == "Finished")
            {
                break;
            }

            if (NextAction(view) is { } action)
            {
                Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/games/poker/tables/{tableId}/action", new StringContent(action, Encoding.UTF8, "application/json"))).StatusCode);
            }
            else
            {
                await tables.TickAsync(); // le toca al bot
                await Task.Delay(20);
            }
        }

        var payouts = view.GetProperty("payouts").EnumerateArray().Select(p => p.GetInt64()).ToList();
        Assert.Equal(3 * BuyIn, payouts.Sum()); // lo que se puso entre los tres
        var mine = payouts[view.GetProperty("mySeat").GetInt32()];
        Assert.True(view.GetProperty("game").GetProperty("done").GetBoolean());
        await WaitUntilAsync(async () => await BalanceAsync(cluster, accountId) == (1_000 - BuyIn + mine, 0), "el pago liquidado");
    }
}
