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
/// La mesa libre del Poker de punta a punta: la mesa nace en juego esperando jugadores, cada uno se sienta con las fichas que quiere (dentro del rango), la mano arranca sola con dos, se recarga entre manos,
/// cada uno se levanta cobrando su pila (se liquida su reserva en la Wallet sin esperar al resto) y la casa se queda con el rake de los pozos. Cuando se va el ultimo, o la mesa queda inactiva, termina.
/// </summary>
[Collection(WalletDbDefinition.Name)]
public sealed class PokerCashFlowTests(PostgresFixture db, RabbitMqFixture rabbit) : IDisposable
{
    private const long Initial = 1_000;

    private readonly List<CasinoCluster> _clusters = [];

    public void Dispose()
    {
        foreach (var cluster in _clusters)
        {
            cluster.Dispose();
        }
    }

    private sealed record Player(HttpClient Client, Guid AccountId);

    private async Task<(CasinoCluster Cluster, Player A, Player B)> StartAsync(long bBalance = Initial, Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder>? customize = null)
    {
        var cluster = TestAuth.StartApp(db, rabbit, customize: customize);
        _clusters.Add(cluster);
        var wallet = cluster.Wallet.Services.GetRequiredService<WalletService>();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var accountA = await wallet.OpenAccountAsync(userA);
        var accountB = await wallet.OpenAccountAsync(userB);
        await wallet.CreditAsync(accountA, "initial-a", Initial);
        await wallet.CreditAsync(accountB, "initial-b", bBalance);
        return (cluster, new Player(cluster.ClientFor(userA), accountA), new Player(cluster.ClientFor(userB), accountB));
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

    private static bool MyTurnToPlay(JsonElement view) =>
        view.GetProperty("status").GetString() == "Playing"
        && view.GetProperty("game").GetProperty("done").ValueKind == JsonValueKind.False
        && view.GetProperty("turnSeat").ValueKind == JsonValueKind.Number
        && view.GetProperty("turnSeat").GetInt32() == view.GetProperty("mySeat").GetInt32();

    private static async Task<Guid> CreateCashTableAsync(HttpClient client, long entry = 100, string key = "cash-1")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/games/poker/tables") { Content = JsonContent.Create(new { buyIn = entry, maxPlayers = 3, options = new { mode = 1, bigBlind = 4 } }) };
        request.Headers.Add("Idempotency-Key", key);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tableId").GetGuid();
    }

    private static async Task<HttpStatusCode> JoinAsync(HttpClient client, Guid tableId, long amount) =>
        (await client.PostAsJsonAsync($"/games/poker/tables/{tableId}/join", new { amount })).StatusCode;

    private static async Task<HttpStatusCode> TopUpAsync(HttpClient client, Guid tableId, long amount, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/games/poker/tables/{tableId}/topup") { Content = JsonContent.Create(new { amount }) };
        request.Headers.Add("Idempotency-Key", key);
        return (await client.SendAsync(request)).StatusCode;
    }

    /// <summary>Los dos sentados con sus entradas y la primera mano en marcha.</summary>
    private static async Task<Guid> SeatBothAsync(Player a, Player b, long entryA = 100, long entryB = 200)
    {
        var tableId = await CreateCashTableAsync(a.Client, entryA);
        Assert.Equal(HttpStatusCode.Accepted, await JoinAsync(b.Client, tableId, entryB));
        await WaitUntilAsync(async () => (await ViewAsync(a.Client, tableId)).GetProperty("game") is { ValueKind: JsonValueKind.Object } g && g.GetProperty("handNo").GetInt32() == 1 && !g.GetProperty("waiting").GetBoolean(), "la primera mano");
        return tableId;
    }

    [Fact]
    public async Task A_cash_table_is_born_in_play_waiting_for_players_and_shows_up_in_the_list_with_a_free_seat()
    {
        var (cluster, a, b) = await StartAsync();
        var tableId = await CreateCashTableAsync(a.Client);

        var view = await ViewAsync(a.Client, tableId);
        Assert.Equal("Playing", view.GetProperty("status").GetString());
        Assert.True(view.GetProperty("continuous").GetBoolean());
        Assert.Equal((80L, 400L), (view.GetProperty("minEntry").GetInt64(), view.GetProperty("maxEntry").GetInt64()));
        Assert.Equal(3, view.GetProperty("seats").GetArrayLength());
        Assert.Contains("Mesa libre", view.GetProperty("description").GetString());
        await WaitUntilAsync(async () => !(await ViewAsync(a.Client, tableId)).GetProperty("game").GetProperty("players")[0].GetProperty("vacant").GetBoolean(), "el creador sentado");
        Assert.Equal((Initial - 100, 100L), await BalanceAsync(cluster, a.AccountId)); // se reservo su entrada, sin comision
        Assert.True((await ViewAsync(a.Client, tableId)).GetProperty("game").GetProperty("waiting").GetBoolean()); // uno solo no alcanza
        Assert.Equal(JsonValueKind.Null, (await ViewAsync(a.Client, tableId)).GetProperty("turnSeat").ValueKind);

        var list = await b.Client.GetFromJsonAsync<JsonElement>("/games/poker/tables");
        var listed = list.EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == tableId);
        Assert.True(listed.GetProperty("continuous").GetBoolean());
        Assert.Equal(1, listed.GetProperty("players").GetInt32());
    }

    [Fact]
    public async Task Entries_outside_the_range_are_refused_and_someone_without_enough_chips_does_not_keep_the_seat()
    {
        var (cluster, a, b) = await StartAsync(bBalance: 50);
        var tableId = await CreateCashTableAsync(a.Client);

        Assert.Equal(HttpStatusCode.BadRequest, await JoinAsync(b.Client, tableId, 79)); // por debajo de 20 ciegas grandes
        Assert.Equal(HttpStatusCode.BadRequest, await JoinAsync(b.Client, tableId, 401));
        Assert.Equal(HttpStatusCode.NotFound, await TopUpAsync(b.Client, tableId, 50, "x")); // no esta sentado

        Assert.Equal(HttpStatusCode.Accepted, await JoinAsync(b.Client, tableId, 100)); // pero no le alcanza: la Wallet rechaza
        await WaitUntilAsync(async () => (await ViewAsync(a.Client, tableId)).GetProperty("seats")[1].GetProperty("vacant").GetBoolean(), "el asiento liberado tras el rechazo");
        Assert.Equal((50L, 0L), await BalanceAsync(cluster, b.AccountId));
        Assert.True((await ViewAsync(a.Client, tableId)).GetProperty("game").GetProperty("waiting").GetBoolean());
    }

    [Fact]
    public async Task Two_players_play_an_all_in_the_house_takes_the_rake_and_each_one_is_paid_when_leaving_until_the_table_closes()
    {
        var (cluster, a, b) = await StartAsync();
        var tableId = await SeatBothAsync(a, b);
        Player[] players = [a, b];

        // Mano 1: all-in y showdown. El pozo es de 200 (la pila corta) y la casa se queda con el 3 %.
        await WaitUntilAsync(async () =>
        {
            foreach (var player in players)
            {
                var mine = await ViewAsync(player.Client, tableId);
                if (mine.GetProperty("game").GetProperty("done").GetBoolean())
                {
                    return true;
                }

                if (MyTurnToPlay(mine))
                {
                    var game = mine.GetProperty("game");
                    var actions = game.GetProperty("actions").EnumerateArray().Select(x => x.GetString()).ToList();
                    var json = actions.Contains("raise")
                        ? $$"""{"type":"raise","to":{{game.GetProperty("maxRaiseTo").GetInt64()}}}"""
                        : actions.Contains("check") ? """{"type":"check"}""" : """{"type":"call"}""";
                    Assert.Equal(HttpStatusCode.NoContent, (await ActAsync(player.Client, tableId, json)).StatusCode);
                }
            }

            return false;
        }, "el all-in resuelto");

        var done = (await ViewAsync(a.Client, tableId)).GetProperty("game");
        var rake = done.GetProperty("rakeTotal").GetInt64();
        Assert.Equal(6L, rake); // 3 % de 200
        Assert.Equal(300L - 6, done.GetProperty("players").EnumerateArray().Sum(p => p.GetProperty("stack").GetInt64())); // 100 + 200 de entradas menos el rake

        // Cada uno se levanta cobrando su pila: se liquida su reserva sin esperar al otro.
        Assert.Equal(HttpStatusCode.NoContent, (await a.Client.PostAsync($"/games/poker/tables/{tableId}/leave", content: null)).StatusCode);
        await WaitUntilAsync(async () => (await BalanceAsync(cluster, a.AccountId)).Reserved == 0, "la reserva de A liquidada");
        var afterA = await ViewAsync(b.Client, tableId);
        Assert.Equal("Playing", afterA.GetProperty("status").GetString()); // B sigue sentado: la mesa no termino
        Assert.True(afterA.GetProperty("seats")[0].GetProperty("vacant").GetBoolean());
        Assert.Equal(200L, (await BalanceAsync(cluster, b.AccountId)).Reserved);

        Assert.Equal(HttpStatusCode.NoContent, (await b.Client.PostAsync($"/games/poker/tables/{tableId}/leave", content: null)).StatusCode);
        await WaitUntilAsync(async () => (await BalanceAsync(cluster, b.AccountId)).Reserved == 0, "la reserva de B liquidada");

        // Se fue el ultimo: la mesa termina, se revela la semilla y todo lo que se puso es lo que se llevaron mas el rake.
        await WaitUntilAsync(async () => (await ViewAsync(b.Client, tableId)).GetProperty("status").GetString() == "Finished", "la mesa terminada");
        Assert.NotEqual(JsonValueKind.Null, (await ViewAsync(b.Client, tableId)).GetProperty("serverSeed").ValueKind);
        var balanceA = (await BalanceAsync(cluster, a.AccountId)).Available;
        var balanceB = (await BalanceAsync(cluster, b.AccountId)).Available;
        Assert.Equal((2 * Initial) - rake, balanceA + balanceB);
    }

    [Fact]
    public async Task A_top_up_is_reserved_in_the_wallet_enters_the_stack_at_the_next_hand_and_is_paid_with_the_rest_when_leaving()
    {
        var (cluster, a, b) = await StartAsync();
        var tableId = await SeatBothAsync(a, b);
        var tables = cluster.Games.Services.GetRequiredService<TableService>();
        Player[] players = [a, b];

        // Se termina la mano 1 retirandose el que tiene el turno (sin flop, sin rake).
        await WaitUntilAsync(async () =>
        {
            foreach (var player in players)
            {
                var mine = await ViewAsync(player.Client, tableId);
                if (MyTurnToPlay(mine))
                {
                    Assert.Equal(HttpStatusCode.NoContent, (await ActAsync(player.Client, tableId, """{"type":"fold"}""")).StatusCode);
                    return true;
                }
            }

            return false;
        }, "la mano retirada");

        // B recarga 100: se reservan en la Wallet y quedan pendientes (la pila no cambia en plena pausa ni en una mano).
        Assert.Equal(HttpStatusCode.Accepted, await TopUpAsync(b.Client, tableId, 100, "top-1"));
        await WaitUntilAsync(async () => (await BalanceAsync(cluster, b.AccountId)).Reserved == 300, "la recarga reservada");
        var pending = (await ViewAsync(b.Client, tableId)).GetProperty("game").GetProperty("players").EnumerateArray().Single(p => p.GetProperty("pendingChips").GetInt64() > 0);
        Assert.Equal(100L, pending.GetProperty("pendingChips").GetInt64());
        Assert.Equal(HttpStatusCode.Accepted, await TopUpAsync(b.Client, tableId, 100, "top-1")); // mismo pedido: idempotente, no reserva de nuevo
        Assert.Equal(300L, (await BalanceAsync(cluster, b.AccountId)).Reserved);
        Assert.Equal(HttpStatusCode.BadRequest, await TopUpAsync(b.Client, tableId, 500, "top-big")); // mas que la pila maxima

        // Pasa la pausa y arranca la mano 2: la recarga entra a la pila.
        await WaitUntilAsync(async () =>
        {
            await tables.TickAsync();
            return (await ViewAsync(b.Client, tableId)).GetProperty("game").GetProperty("handNo").GetInt32() == 2;
        }, "la mano siguiente", seconds: 30);
        var seatB = (await ViewAsync(b.Client, tableId)).GetProperty("mySeat").GetInt32();
        var game = (await ViewAsync(b.Client, tableId)).GetProperty("game");
        var mineNow = game.GetProperty("players")[seatB];
        Assert.Equal(0L, mineNow.GetProperty("pendingChips").GetInt64());
        Assert.True(mineNow.GetProperty("stack").GetInt64() + mineNow.GetProperty("bet").GetInt64() >= 290); // 200 + 100 menos, a lo sumo, una ciega

        // Se levantan los dos (el de la mano en marcha sigue en automatico y se va al terminar): sin rake, la suma de lo que cobran es lo que pusieron.
        Assert.Equal(HttpStatusCode.NoContent, (await a.Client.PostAsync($"/games/poker/tables/{tableId}/leave", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await b.Client.PostAsync($"/games/poker/tables/{tableId}/leave", content: null)).StatusCode);
        await WaitUntilAsync(async () =>
        {
            await tables.TickAsync();
            return (await BalanceAsync(cluster, a.AccountId)).Reserved == 0 && (await BalanceAsync(cluster, b.AccountId)).Reserved == 0;
        }, "las dos reservas liquidadas", seconds: 60);
        await WaitUntilAsync(async () => (await ViewAsync(a.Client, tableId)).GetProperty("status").GetString() == "Finished", "la mesa terminada");
        Assert.Equal(2 * Initial, (await BalanceAsync(cluster, a.AccountId)).Available + (await BalanceAsync(cluster, b.AccountId)).Available);
    }

    [Fact]
    public async Task A_cash_table_left_waiting_for_too_long_is_closed_and_the_player_gets_the_chips_back()
    {
        var (cluster, a, _) = await StartAsync(customize: builder => builder.UseSetting("Tables:CashIdleMinutes", "0.05"));
        var tables = cluster.Games.Services.GetRequiredService<TableService>();
        var tableId = await CreateCashTableAsync(a.Client);
        await WaitUntilAsync(async () => (await BalanceAsync(cluster, a.AccountId)).Reserved == 100, "la entrada reservada");
        await WaitUntilAsync(async () => !(await ViewAsync(a.Client, tableId)).GetProperty("game").GetProperty("players")[0].GetProperty("vacant").GetBoolean(), "el creador sentado");

        await Task.Delay(4_000); // pasa el limite de inactividad (3 s)
        await WaitUntilAsync(async () =>
        {
            await tables.TickAsync();
            return (await ViewAsync(a.Client, tableId)).GetProperty("status").GetString() == "Finished";
        }, "la mesa cerrada por inactividad", seconds: 30);

        await WaitUntilAsync(async () => await BalanceAsync(cluster, a.AccountId) == (Initial, 0), "las fichas devueltas");
    }
}
