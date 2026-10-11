using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casino.Integration.Tests.Wallet;
using Casino.Modules.Games.Fairness;
using Casino.Modules.Games.Tables;
using Casino.Modules.Wallet.Application;
using Marten;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Casino.Integration.Tests.Games;

/// <summary>
/// La plataforma de mesas entre jugadores de punta a punta (con Uno como juego), con Postgres y RabbitMQ reales: crear, entrar, bots, iniciar, jugar por turnos, turnos vencidos,
/// mesas privadas, manos ocultas, el pago contra la Wallet y el saldo que siempre cierra.
/// </summary>
[Collection(WalletDbDefinition.Name)]
public sealed class TablesFlowTests(PostgresFixture db, RabbitMqFixture rabbit) : IDisposable
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

    private CasinoCluster Start(double openTableMinutes = 15)
    {
        var cluster = TestAuth.StartApp(db, rabbit, customize: builder =>
        {
            builder.UseSetting("Tables:BotThinkMilliseconds", "0");
            builder.UseSetting("Tables:OpenTableMinutes", openTableMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });
        _clusters.Add(cluster);
        return cluster;
    }

    private static TableService Tables(CasinoCluster cluster) => cluster.Games.Services.GetRequiredService<TableService>();

    private static IDocumentStore Store(CasinoCluster cluster) => cluster.Games.Services.GetRequiredService<IDocumentStore>();

    private static async Task<(Guid UserId, Guid AccountId, HttpClient Client)> PlayerAsync(CasinoCluster cluster, long chips)
    {
        var userId = Guid.NewGuid();
        var wallet = cluster.Wallet.Services.GetRequiredService<WalletService>();
        var accountId = await wallet.OpenAccountAsync(userId);
        if (chips > 0)
        {
            await wallet.CreditAsync(accountId, "initial-credit", chips);
        }

        return (userId, accountId, cluster.ClientFor(userId));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, int seconds = 40)
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

    private static async Task<(long Available, long Reserved)> BalanceAsync(CasinoCluster cluster, Guid accountId)
    {
        var account = await cluster.Wallet.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        return (account.Available, account.Reserved);
    }

    private static Task<HttpResponseMessage> TryCreateAsync(HttpClient client, string? key, int maxPlayers = 3, bool isPrivate = false, long buyIn = BuyIn, string? name = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/games/uno/tables") { Content = JsonContent.Create(new { buyIn, maxPlayers, isPrivate, name }) };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return client.SendAsync(request);
    }

    private static async Task<Guid> CreateAsync(HttpClient client, string key, int maxPlayers = 3, bool isPrivate = false, long buyIn = BuyIn, string? name = null)
    {
        var response = await TryCreateAsync(client, key, maxPlayers, isPrivate, buyIn, name);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tableId").GetGuid();
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid tableId, string path, object? body = null) =>
        client.PostAsync($"/games/uno/tables/{tableId}/{path}", body is null ? null : JsonContent.Create(body));

    private static Task<HttpResponseMessage> ActAsync(HttpClient client, Guid tableId, string json) =>
        client.PostAsync($"/games/uno/tables/{tableId}/action", new StringContent(json, Encoding.UTF8, "application/json"));

    private static Task<JsonElement> ViewAsync(HttpClient client, Guid tableId) => client.GetFromJsonAsync<JsonElement>($"/games/uno/tables/{tableId}");

    /// <summary>Espera a que las fichas de todos los humanos esten reservadas y arranca la partida.</summary>
    private static async Task StartWhenReadyAsync(HttpClient owner, Guid tableId)
    {
        await WaitUntilAsync(async () => (await ViewAsync(owner, tableId)).GetProperty("seats").EnumerateArray().All(s => s.GetProperty("ready").GetBoolean()), "las fichas reservadas");
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(owner, tableId, "start")).StatusCode);
    }

    /// <summary>La jugada que haria un jugador simple con lo que ve: juega la primera carta posible; si no, roba y pasa.</summary>
    private static string? NextAction(JsonElement view)
    {
        var game = view.GetProperty("game");
        if (view.GetProperty("turnSeat").ValueKind != JsonValueKind.Number || view.GetProperty("turnSeat").GetInt32() != view.GetProperty("mySeat").GetInt32())
        {
            return null;
        }

        var playable = game.GetProperty("playable").EnumerateArray().Select(c => c.GetInt32()).ToList();
        if (game.GetProperty("drawnCard").ValueKind == JsonValueKind.Number)
        {
            return playable.Count > 0 ? PlayJson(playable[0]) : """{"type":"pass"}""";
        }

        return playable.Count > 0 ? PlayJson(playable[0]) : """{"type":"draw"}""";
    }

    private static string PlayJson(int card) => card >= 100 ? $$"""{"type":"play","card":{{card}},"color":0}""" : $$"""{"type":"play","card":{{card}}}""";

    /// <summary>Juega la partida entera: cada jugador actua cuando le toca y el motor juega por los bots.</summary>
    private static async Task PlayToTheEndAsync(CasinoCluster cluster, Guid tableId, params HttpClient[] humans)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            var views = new List<JsonElement>();
            foreach (var human in humans)
            {
                views.Add(await ViewAsync(human, tableId));
            }

            if (views[0].GetProperty("status").GetString() == "Finished")
            {
                return;
            }

            var acted = false;
            for (var i = 0; i < humans.Length; i++)
            {
                if (NextAction(views[i]) is { } action)
                {
                    Assert.Equal(HttpStatusCode.NoContent, (await ActAsync(humans[i], tableId, action)).StatusCode);
                    acted = true;
                }
            }

            if (!acted)
            {
                await Tables(cluster).TickAsync(); // le toca a un bot: juega el motor
                await Task.Delay(30);
            }
        }

        throw new TimeoutException("La partida no termino a tiempo.");
    }

    [Fact]
    public async Task A_player_creates_a_table_adds_two_bots_plays_the_game_and_the_balance_closes()
    {
        using var cluster = Start();
        var (_, accountId, client) = await PlayerAsync(cluster, 1_000);
        using var _ = client;

        var tableId = await CreateAsync(client, "solo-1");
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(client, tableId, "bots")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(client, tableId, "bots")).StatusCode);
        await WaitUntilAsync(async () => await BalanceAsync(cluster, accountId) == (1_000 - BuyIn, BuyIn), "mis fichas reservadas");
        await StartWhenReadyAsync(client, tableId);

        await PlayToTheEndAsync(cluster, tableId, client);

        var view = await ViewAsync(client, tableId);
        var payouts = view.GetProperty("payouts").EnumerateArray().Select(p => p.GetInt64()).ToList();
        Assert.Equal(3 * BuyIn, payouts.Sum()); // lo que se puso entre los tres
        var mine = payouts[view.GetProperty("mySeat").GetInt32()];
        Assert.Contains(mine, new[] { 0L, 3 * BuyIn });
        await WaitUntilAsync(async () => await BalanceAsync(cluster, accountId) == (1_000 - BuyIn + mine, 0), "el pago liquidado");

        // Provably fair: la semilla revelada coincide con el compromiso publicado antes de que nadie se sentara.
        Assert.True(ProvablyFair.MatchesCommitment(view.GetProperty("serverSeed").GetString()!, view.GetProperty("commitment").GetString()!));
        var history = await client.GetFromJsonAsync<JsonElement>("/games/uno/history");
        Assert.Contains(history.EnumerateArray(), t => t.GetProperty("id").GetGuid() == tableId);
    }

    [Fact]
    public async Task Two_players_and_a_bot_share_a_table_each_sees_only_their_own_hand_and_both_are_paid()
    {
        using var cluster = Start();
        var (_, accountOne, one) = await PlayerAsync(cluster, 1_000);
        var (_, accountTwo, two) = await PlayerAsync(cluster, 1_000);
        using var __ = one;
        using var ___ = two;

        var tableId = await CreateAsync(one, "duo-1");
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(two, tableId, "join")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(two, tableId, "join")).StatusCode); // reintento: idempotente
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(one, tableId, "bots")).StatusCode);
        await StartWhenReadyAsync(one, tableId);

        // Cada uno ve su mano; la del otro no viaja (solo cuantas cartas tiene).
        var viewOne = await ViewAsync(one, tableId);
        var viewTwo = await ViewAsync(two, tableId);
        var handOne = viewOne.GetProperty("game").GetProperty("hand").EnumerateArray().Select(c => c.GetInt32()).ToList();
        var handTwo = viewTwo.GetProperty("game").GetProperty("hand").EnumerateArray().Select(c => c.GetInt32()).ToList();
        Assert.NotEmpty(handOne);
        Assert.NotEmpty(handTwo);
        Assert.Empty(handOne.Intersect(handTwo));
        Assert.Equal(1, viewOne.GetProperty("seats").EnumerateArray().Count(s => s.GetProperty("mine").GetBoolean()));
        Assert.Equal(3, viewOne.GetProperty("game").GetProperty("players").GetArrayLength());
        Assert.DoesNotContain("encryptedSeed", viewOne.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JsonValueKind.Null, viewOne.GetProperty("serverSeed").ValueKind); // la semilla recien se revela al terminar
        Assert.All(viewOne.GetProperty("seats").EnumerateArray(), s => Assert.StartsWith(s.GetProperty("isBot").GetBoolean() ? "Bot " : "Jugador ", s.GetProperty("name").GetString())); // sin identidades

        await PlayToTheEndAsync(cluster, tableId, one, two);

        var view = await ViewAsync(one, tableId);
        var payouts = view.GetProperty("payouts").EnumerateArray().Select(p => p.GetInt64()).ToList();
        Assert.Equal(3 * BuyIn, payouts.Sum());
        var seatOne = viewOne.GetProperty("mySeat").GetInt32();
        var seatTwo = viewTwo.GetProperty("mySeat").GetInt32();
        await WaitUntilAsync(async () => await BalanceAsync(cluster, accountOne) == (1_000 - BuyIn + payouts[seatOne], 0), "el pago del primero");
        await WaitUntilAsync(async () => await BalanceAsync(cluster, accountTwo) == (1_000 - BuyIn + payouts[seatTwo], 0), "el pago del segundo");
    }

    [Fact]
    public async Task The_rules_of_the_table_are_enforced_for_joining_starting_leaving_and_acting()
    {
        using var cluster = Start();
        var (_, _, one) = await PlayerAsync(cluster, 1_000);
        var (_, accountTwo, two) = await PlayerAsync(cluster, 1_000);
        var (_, _, three) = await PlayerAsync(cluster, 1_000);
        using var __ = one;
        using var ___ = two;
        using var ____ = three;

        // Validaciones de la mesa.
        Assert.Equal(HttpStatusCode.BadRequest, (await TryCreateAsync(one, "rules-low", buyIn: 5)).StatusCode); // entrada fuera de rango
        Assert.Equal(HttpStatusCode.BadRequest, (await TryCreateAsync(one, "rules-big", maxPlayers: 9)).StatusCode); // demasiados jugadores
        Assert.Equal(HttpStatusCode.BadRequest, (await TryCreateAsync(one, null)).StatusCode); // sin Idempotency-Key
        var tableId = await CreateAsync(one, "rules-1", maxPlayers: 2);
        Assert.Equal(tableId, await CreateAsync(one, "rules-1", maxPlayers: 2)); // el mismo pedido: la misma mesa
        Assert.Equal(HttpStatusCode.Conflict, (await TryCreateAsync(one, "rules-other")).StatusCode); // ya esta sentado en una mesa de Uno

        // Iniciar: solo el dueño y con el minimo de jugadores.
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(one, tableId, "start")).StatusCode); // 1 asiento: faltan jugadores
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(two, tableId, "join")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(three, tableId, "join")).StatusCode); // llena
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(two, tableId, "start")).StatusCode); // no es el dueño
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(two, tableId, "bots")).StatusCode);

        // Salir antes de empezar devuelve las fichas.
        await WaitUntilAsync(async () => await BalanceAsync(cluster, accountTwo) == (1_000 - BuyIn, BuyIn), "las fichas del segundo reservadas");
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(two, tableId, "leave")).StatusCode);
        await WaitUntilAsync(async () => await BalanceAsync(cluster, accountTwo) == (1_000, 0), "las fichas del segundo devueltas");
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(three, tableId, "join")).StatusCode); // ahora hay lugar

        // Ya en juego: no se puede salir, solo juega quien tiene el turno, y una jugada invalida es un error claro.
        await StartWhenReadyAsync(one, tableId);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(three, tableId, "leave")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(one, tableId, "start")).StatusCode);
        var view = await ViewAsync(one, tableId);
        var turn = view.GetProperty("turnSeat").GetInt32();
        var mySeat = view.GetProperty("mySeat").GetInt32();
        var (notMyTurn, itsPlayer) = turn == mySeat ? (three, true) : (one, false);
        Assert.Equal(HttpStatusCode.Conflict, (await ActAsync(notMyTurn, tableId, """{"type":"draw"}""")).StatusCode);
        var current = itsPlayer ? one : three;
        Assert.Equal(HttpStatusCode.BadRequest, (await ActAsync(current, tableId, """{"type":"pass"}""")).StatusCode); // no se puede pasar sin robar
        Assert.Equal(HttpStatusCode.BadRequest, (await ActAsync(current, tableId, "esto no es una jugada")).StatusCode);
    }

    [Fact]
    public async Task A_private_table_is_hidden_from_the_list_and_needs_its_code()
    {
        using var cluster = Start();
        var (_, _, owner) = await PlayerAsync(cluster, 1_000);
        var (_, _, guest) = await PlayerAsync(cluster, 1_000);
        var (_, _, stranger) = await PlayerAsync(cluster, 1_000);
        using var __ = owner;
        using var ___ = guest;
        using var ____ = stranger;

        var request = new HttpRequestMessage(HttpMethod.Post, "/games/uno/tables") { Content = JsonContent.Create(new { buyIn = BuyIn, maxPlayers = 3, isPrivate = true, name = "Los pibes" }) };
        request.Headers.Add("Idempotency-Key", "private-1");
        var created = await (await owner.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();
        var tableId = created.GetProperty("tableId").GetGuid();
        var code = created.GetProperty("joinCode").GetString()!;
        Assert.Equal(6, code.Length);

        var publicList = await stranger.GetFromJsonAsync<JsonElement>("/games/uno/tables");
        Assert.DoesNotContain(publicList.EnumerateArray(), t => t.GetProperty("id").GetGuid() == tableId);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/games/uno/tables/{tableId}")).StatusCode); // ni se revela que existe
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(guest, tableId, "join", new { code = "ZZZZZZ" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(guest, tableId, "join")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(guest, tableId, "join", new { code = code.ToLowerInvariant() })).StatusCode);

        // Sin conocer el id: con solo el codigo se encuentra la mesa (en cualquier mayuscula/minuscula); uno equivocado no.
        Assert.Equal(HttpStatusCode.BadRequest, (await stranger.PostAsJsonAsync("/games/uno/tables/join", new { code = "ZZZZZZ" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await stranger.PostAsJsonAsync("/games/uno/tables/join", new { code = "x" })).StatusCode);
        var byCode = await stranger.PostAsJsonAsync("/games/uno/tables/join", new { code = code.ToLowerInvariant() });
        Assert.Equal(HttpStatusCode.Accepted, byCode.StatusCode);
        Assert.Equal(tableId, (await byCode.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tableId").GetGuid());
        Assert.Equal(3, (await ViewAsync(owner, tableId)).GetProperty("seats").GetArrayLength());

        var ownerView = await ViewAsync(owner, tableId);
        var guestView = await ViewAsync(guest, tableId);
        Assert.Equal(code, ownerView.GetProperty("joinCode").GetString());
        Assert.Equal(JsonValueKind.Null, guestView.GetProperty("joinCode").ValueKind); // el codigo lo ve solo el dueño
        Assert.Equal("Los pibes", ownerView.GetProperty("name").GetString());
    }

    [Fact]
    public async Task An_open_public_table_shows_up_in_the_list_with_who_is_waiting()
    {
        using var cluster = Start();
        var (_, _, owner) = await PlayerAsync(cluster, 1_000);
        var (_, _, other) = await PlayerAsync(cluster, 1_000);
        using var __ = owner;
        using var ___ = other;
        var tableId = await CreateAsync(owner, "list-1", maxPlayers: 4, name: "Mesa abierta");
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(owner, tableId, "bots")).StatusCode);

        var list = await other.GetFromJsonAsync<JsonElement>("/games/uno/tables");

        var summary = Assert.Single(list.EnumerateArray(), t => t.GetProperty("id").GetGuid() == tableId);
        Assert.Equal(("Mesa abierta", 4, 1, 1, false), (summary.GetProperty("name").GetString(), summary.GetProperty("maxPlayers").GetInt32(), summary.GetProperty("players").GetInt32(), summary.GetProperty("bots").GetInt32(), summary.GetProperty("mine").GetBoolean()));
        var rules = await other.GetFromJsonAsync<JsonElement>("/games/uno/rules");
        Assert.Equal((2, 6), (rules.GetProperty("minPlayers").GetInt32(), rules.GetProperty("maxPlayers").GetInt32()));
    }

    [Fact]
    public async Task A_player_without_enough_chips_loses_the_seat_and_nothing_is_taken()
    {
        using var cluster = Start();
        var (_, _, owner) = await PlayerAsync(cluster, 1_000);
        var (_, poorAccount, poor) = await PlayerAsync(cluster, 10);
        using var __ = owner;
        using var ___ = poor;
        var tableId = await CreateAsync(owner, "poor-1");

        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(poor, tableId, "join")).StatusCode);

        await WaitUntilAsync(async () => (await ViewAsync(owner, tableId)).GetProperty("seats").GetArrayLength() == 1, "el asiento liberado por falta de fichas");
        Assert.Equal((10L, 0L), await BalanceAsync(cluster, poorAccount));
    }

    [Fact]
    public async Task A_player_who_lets_the_turn_expire_is_played_for_and_after_three_misses_a_bot_takes_over()
    {
        using var cluster = Start();
        var (_, _, owner) = await PlayerAsync(cluster, 1_000);
        using var __ = owner;
        var tableId = await CreateAsync(owner, "idle-1", maxPlayers: 2);
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(owner, tableId, "bots")).StatusCode);
        await StartWhenReadyAsync(owner, tableId);

        // El turno del jugador vence tres veces seguidas (se adelanta el reloj en la base para no esperar 30 s cada vez).
        for (var misses = 0; misses < 3; misses++)
        {
            await WaitUntilAsync(async () =>
            {
                var v = await ViewAsync(owner, tableId);
                if (v.GetProperty("turnSeat").GetInt32() == v.GetProperty("mySeat").GetInt32())
                {
                    return true;
                }

                await Tables(cluster).TickAsync(); // juega el bot
                return false;
            }, "el turno del jugador");
            await ExpireAsync(cluster, tableId);
            await Tables(cluster).TickAsync();
        }

        var seats = (await ViewAsync(owner, tableId)).GetProperty("seats").EnumerateArray().ToList();
        Assert.True(seats.Single(s => s.GetProperty("mine").GetBoolean()).GetProperty("away").GetBoolean()); // ausente: lo juega un bot
        await WaitUntilAsync(async () =>
        {
            await Tables(cluster).TickAsync(); // el humano ya no actua: el motor juega por el y por el bot hasta el final
            return (await ViewAsync(owner, tableId)).GetProperty("status").GetString() == "Finished";
        }, "la partida terminada sola", seconds: 90);
    }

    [Fact]
    public async Task An_open_table_that_nobody_starts_is_cancelled_and_the_chips_come_back()
    {
        using var cluster = Start(openTableMinutes: 0.05);
        var (_, accountId, owner) = await PlayerAsync(cluster, 1_000);
        using var __ = owner;
        var tableId = await CreateAsync(owner, "stale-1");
        await WaitUntilAsync(async () => await BalanceAsync(cluster, accountId) == (1_000 - BuyIn, BuyIn), "las fichas reservadas");

        await Task.Delay(TimeSpan.FromSeconds(4)); // pasa el plazo de 3 s
        await Tables(cluster).TickAsync();

        Assert.Equal("Cancelled", (await ViewAsync(owner, tableId)).GetProperty("status").GetString());
        await WaitUntilAsync(async () => await BalanceAsync(cluster, accountId) == (1_000, 0), "las fichas devueltas");
    }

    /// <summary>Hace vencer el turno actual de una mesa (adelanta su reloj en la base de datos).</summary>
    private static async Task ExpireAsync(CasinoCluster cluster, Guid tableId)
    {
        await using var session = Store(cluster).LightweightSession();
        var table = (await session.LoadAsync<PlayerTable>(tableId))!;
        table.NextAutoAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        table.TurnEndsAt = table.NextAutoAt;
        session.Store(table);
        await session.SaveChangesAsync();
    }
}
