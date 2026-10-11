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

/// <summary>Truco sobre la plataforma de mesas, de punta a punta: una persona contra un bot, con envido, truco y el pago contra la Wallet.</summary>
[Collection(WalletDbDefinition.Name)]
public sealed class TrucoFlowTests(PostgresFixture db, RabbitMqFixture rabbit) : IDisposable
{
    private const long BuyIn = 50;

    private readonly List<CasinoCluster> _clusters = [];

    public void Dispose()
    {
        foreach (var cluster in _clusters)
        {
            cluster.Dispose();
        }
    }

    private static async Task<JsonElement> ViewAsync(HttpClient client, Guid tableId) => await client.GetFromJsonAsync<JsonElement>($"/games/truco/tables/{tableId}");

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

    /// <summary>La jugada de un jugador simple: si le cantan, no quiere; si no, tira su carta mas baja... la primera de la mano.</summary>
    private static string? NextAction(JsonElement view)
    {
        if (view.GetProperty("turnSeat").ValueKind != JsonValueKind.Number || view.GetProperty("turnSeat").GetInt32() != view.GetProperty("mySeat").GetInt32())
        {
            return null;
        }

        var game = view.GetProperty("game");
        var actions = game.GetProperty("actions").EnumerateArray().Select(a => a.GetString()).ToList();
        if (actions.Contains("quiero"))
        {
            return actions.Contains("no_quiero") && game.GetProperty("pending").GetProperty("kind").GetString() == "truco" ? """{"type":"no_quiero"}""" : """{"type":"quiero"}""";
        }

        return $$"""{"type":"play","card":{{game.GetProperty("hand")[0].GetInt32()}}}""";
    }

    [Fact]
    public async Task A_player_plays_a_full_truco_game_against_a_bot_and_the_balance_closes()
    {
        var cluster = TestAuth.StartApp(db, rabbit, customize: builder => builder.UseSetting("Tables:BotThinkMilliseconds", "0"));
        _clusters.Add(cluster);
        var userId = Guid.NewGuid();
        var wallet = cluster.Wallet.Services.GetRequiredService<WalletService>();
        var accountId = await wallet.OpenAccountAsync(userId);
        await wallet.CreditAsync(accountId, "initial-credit", 1_000);
        using var client = cluster.ClientFor(userId);
        var tables = cluster.Games.Services.GetRequiredService<TableService>();

        var rules = await client.GetFromJsonAsync<JsonElement>("/games/truco/rules");
        Assert.Equal((2, 2), (rules.GetProperty("minPlayers").GetInt32(), rules.GetProperty("maxPlayers").GetInt32()));
        var request = new HttpRequestMessage(HttpMethod.Post, "/games/truco/tables") { Content = JsonContent.Create(new { buyIn = BuyIn, maxPlayers = 2 }) };
        request.Headers.Add("Idempotency-Key", "truco-1");
        var tableId = (await (await client.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tableId").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/games/truco/tables/{tableId}/bots", content: null)).StatusCode);
        await WaitUntilAsync(async () => (await ViewAsync(client, tableId)).GetProperty("seats").EnumerateArray().All(s => s.GetProperty("ready").GetBoolean()), "las fichas reservadas");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/games/truco/tables/{tableId}/start", content: null)).StatusCode);

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
                Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/games/truco/tables/{tableId}/action", new StringContent(action, Encoding.UTF8, "application/json"))).StatusCode);
            }
            else
            {
                await tables.TickAsync(); // le toca al bot
                await Task.Delay(20);
            }
        }

        var payouts = view.GetProperty("payouts").EnumerateArray().Select(p => p.GetInt64()).Order().ToList();
        Assert.Equal([0L, 2 * BuyIn], payouts);
        var mine = view.GetProperty("payouts")[view.GetProperty("mySeat").GetInt32()].GetInt64();
        var scores = view.GetProperty("game").GetProperty("scores").EnumerateArray().Select(s => s.GetInt32()).ToList();
        Assert.True(scores.Max() >= 15);
        await WaitUntilAsync(async () => await BalanceAsync(cluster, accountId) == (1_000 - BuyIn + mine, 0), "el pago liquidado");
    }
}
