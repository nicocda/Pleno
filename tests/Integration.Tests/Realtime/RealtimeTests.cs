using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casino.BuildingBlocks;
using Casino.Integration.Tests.Wallet;
using Casino.Modules.Wallet.Application;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Casino.Integration.Tests.Realtime;

/// <summary>
/// El canal en vivo de un jugador: SignalR por WebSocket con el token en la query (lo unico que puede hacer un navegador),
/// avisos de saldo y de ronda cerrada que viajan por RabbitMQ, y backplane de Redis entre instancias.
/// </summary>
[Collection(WalletDbDefinition.Name)]
public sealed class RealtimeTests(PostgresFixture db, RabbitMqFixture rabbit, RedisFixture redis, ITestOutputHelper output) : IAsyncLifetime
{
    private readonly List<CasinoCluster> _apps = [];
    private readonly List<HubConnection> _connections = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }

        foreach (var app in _apps)
        {
            app.Dispose();
        }
    }

    private CasinoCluster StartApp(string? redisConnection = null)
    {
        var app = TestAuth.StartApp(db, rabbit, redis: redisConnection);
        _apps.Add(app);
        return app;
    }

    /// <summary>Un cliente SignalR como el del navegador: WebSocket, y el token en la query (?access_token=).</summary>
    private HubConnection Connect(CasinoCluster app, string? token)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, "/hubs/player"), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                // Un navegador no puede poner cabeceras en un WebSocket: manda el token en la query (?access_token=). Se emula igual.
                options.WebSocketFactory = (context, ct) =>
                {
                    var uri = token is null
                        ? context.Uri
                        : new Uri($"{context.Uri}{(string.IsNullOrEmpty(context.Uri.Query) ? "?" : "&")}access_token={Uri.EscapeDataString(token)}");
                    return new ValueTask<System.Net.WebSockets.WebSocket>(app.Server.CreateWebSocketClient().ConnectAsync(uri, ct));
                };
                options.AccessTokenProvider = () => Task.FromResult(token);
            })
            .Build();
        _connections.Add(connection);
        return connection;
    }

    private sealed class Inbox
    {
        public List<JsonElement> Balance { get; } = [];

        public List<JsonElement> Rounds { get; } = [];

        public void Attach(HubConnection connection)
        {
            connection.On<JsonElement>("balanceChanged", notice => { lock (Balance) { Balance.Add(notice); } });
            connection.On<JsonElement>("roundClosed", notice => { lock (Rounds) { Rounds.Add(notice); } });
        }

        public int RoundCount()
        {
            lock (Rounds)
            {
                return Rounds.Count;
            }
        }

        public long LatestVersion()
        {
            lock (Balance)
            {
                return Balance.Count == 0 ? -1 : Balance.Max(n => n.GetProperty("version").GetInt64());
            }
        }
    }

    private static async Task<(Guid UserId, Guid AccountId)> FundedPlayerAsync(CasinoCluster app, long chips)
    {
        var userId = Guid.NewGuid();
        var wallet = app.Services.GetRequiredService<WalletService>();
        var accountId = await wallet.OpenAccountAsync(userId);
        await wallet.CreditAsync(accountId, "initial-credit", chips);
        return (userId, accountId);
    }

    private static async Task<Guid> PlaceAsync(HttpClient client, string key, long stake = 10)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/games/roulette/bets")
        {
            Content = JsonContent.Create(new { betType = "Red", selection = Array.Empty<int>(), stake }),
        };
        request.Headers.Add("Idempotency-Key", key);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("betId").GetGuid();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what, int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"No se cumplio a tiempo: {what}");
    }

    [Fact]
    public async Task A_connection_without_a_token_is_refused()
    {
        var app = StartApp();
        var connection = Connect(app, token: null);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());

        Assert.Contains("401", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_without_the_player_role_is_refused()
    {
        var app = StartApp();
        var connection = Connect(app, TestAuth.Token(Guid.NewGuid(), roles: [Roles.Backoffice]));

        var error = await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());

        Assert.Contains("403", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_garbage_token_in_the_query_is_refused()
    {
        var app = StartApp();
        var connection = Connect(app, "esto.no.es-un-jwt");

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
    }

    [Fact]
    public async Task A_player_receives_balance_and_round_results_live()
    {
        var app = StartApp();
        var (userId, accountId) = await FundedPlayerAsync(app, 1_000);
        using var http = app.ClientFor(userId);
        var inbox = new Inbox();
        var connection = Connect(app, TestAuth.Token(userId));
        inbox.Attach(connection);
        await connection.StartAsync();

        var betId = await PlaceAsync(http, "bet-1", stake: 100);

        await WaitUntilAsync(() => inbox.RoundCount() == 1, "aviso de ronda cerrada");
        JsonElement closed;
        lock (inbox.Rounds)
        {
            closed = inbox.Rounds[0];
        }

        Assert.Equal(betId, closed.GetProperty("betId").GetGuid());
        Assert.Equal("Roulette", closed.GetProperty("game").GetString());
        Assert.Equal("Settled", closed.GetProperty("status").GetString());
        Assert.Equal(100, closed.GetProperty("stake").GetInt64());
        var winning = closed.GetProperty("winningNumber").GetInt32();
        Assert.InRange(winning, 0, 36);

        // El ultimo aviso de saldo (el de mayor version) coincide con la cuenta real.
        var account = await app.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        await WaitUntilAsync(() => inbox.LatestVersion() >= account.Version, "aviso de saldo con la version final");
        JsonElement latest;
        lock (inbox.Balance)
        {
            latest = inbox.Balance.OrderBy(n => n.GetProperty("version").GetInt64()).Last();
        }

        Assert.Equal(account.Available, latest.GetProperty("available").GetInt64());
        Assert.Equal(account.Reserved, latest.GetProperty("reserved").GetInt64());
        Assert.Equal(account.Version, latest.GetProperty("version").GetInt64());
        output.WriteLine($"Salio el {winning}; saldo final {account.Available}; {inbox.Balance.Count} avisos de saldo");
    }

    [Fact]
    public async Task A_rejected_bet_is_reported_with_its_reason()
    {
        var app = StartApp();
        var (userId, _) = await FundedPlayerAsync(app, 20);
        using var http = app.ClientFor(userId);
        var inbox = new Inbox();
        var connection = Connect(app, TestAuth.Token(userId));
        inbox.Attach(connection);
        await connection.StartAsync();

        await PlaceAsync(http, "bet-1", stake: 5_000);

        await WaitUntilAsync(() => inbox.RoundCount() == 1, "aviso de apuesta rechazada");
        lock (inbox.Rounds)
        {
            Assert.Equal("Rejected", inbox.Rounds[0].GetProperty("status").GetString());
            Assert.Equal("InsufficientFunds", inbox.Rounds[0].GetProperty("failureReason").GetString());
            Assert.Equal(0, inbox.Rounds[0].GetProperty("payout").GetInt64());
        }
    }

    [Fact]
    public async Task A_player_never_receives_the_events_of_another_player()
    {
        var app = StartApp();
        var (aliceId, _) = await FundedPlayerAsync(app, 1_000);
        var (bobId, _) = await FundedPlayerAsync(app, 1_000);
        using var alice = app.ClientFor(aliceId);
        var aliceInbox = new Inbox();
        var bobInbox = new Inbox();
        var aliceConnection = Connect(app, TestAuth.Token(aliceId));
        var bobConnection = Connect(app, TestAuth.Token(bobId));
        aliceInbox.Attach(aliceConnection);
        bobInbox.Attach(bobConnection);
        await aliceConnection.StartAsync();
        await bobConnection.StartAsync();

        await PlaceAsync(alice, "bet-1");
        await WaitUntilAsync(() => aliceInbox.RoundCount() == 1, "ronda de Alice");
        await Task.Delay(TimeSpan.FromSeconds(2)); // margen para detectar un aviso mal dirigido

        // Bob no recibe rondas de Alice. Lo unico de saldo que le llega es el de SU cuenta (1.000 fichas, sin reservas),
        // nunca los cambios que provoca la apuesta de Alice (reserva y premio).
        Assert.Equal(0, bobInbox.RoundCount());
        lock (bobInbox.Balance)
        {
            Assert.All(bobInbox.Balance, notice =>
            {
                Assert.Equal(1_000, notice.GetProperty("available").GetInt64());
                Assert.Equal(0, notice.GetProperty("reserved").GetInt64());
            });
        }
    }

    [Fact]
    public async Task The_hub_exposes_no_methods_so_a_client_cannot_join_someone_elses_group()
    {
        var app = StartApp();
        var userId = Guid.NewGuid();
        var connection = Connect(app, TestAuth.Token(userId));
        await connection.StartAsync();
        var victimGroup = $"account:{PlayerIds.WalletAccountFor(Guid.NewGuid()):N}";

        var error = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("AddToGroup", victimGroup));
        Assert.Contains("does not exist", error.Message, StringComparison.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("JoinGroup", victimGroup));
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Subscribe", PlayerIds.WalletAccountFor(Guid.NewGuid())));
    }

    [Fact]
    public async Task Two_instances_share_the_events_through_the_redis_backplane()
    {
        var instanceA = StartApp(redis.ConnectionString);
        var instanceB = StartApp(redis.ConnectionString);
        var (userId, _) = await FundedPlayerAsync(instanceA, 10_000);
        var inbox = new Inbox();
        var connection = Connect(instanceA, TestAuth.Token(userId)); // el navegador esta conectado a la instancia A
        inbox.Attach(connection);
        await connection.StartAsync();
        using var http = instanceB.ClientFor(userId);              // y apuesta por la instancia B

        const int bets = 12;
        for (var i = 0; i < bets; i++)
        {
            await PlaceAsync(http, $"bet-{i}");
        }

        // Ambas instancias consumen la misma cola de RabbitMQ: el aviso lo genera la que tome el mensaje.
        // Con el backplane de Redis todos llegan al navegador, sin importar cual instancia lo haya generado.
        await WaitUntilAsync(() => inbox.RoundCount() == bets, $"los {bets} avisos de ronda", seconds: 90);
        output.WriteLine($"{inbox.RoundCount()} avisos recibidos en la instancia A, apostando por la B");
    }
}
