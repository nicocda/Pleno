using System.Text.Json;
using Casino.Integration.Tests.Wallet;
using Casino.Modules.Games.Application;
using Casino.Modules.Games.Crash;
using Casino.Modules.Games.Fairness;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Casino.Integration.Tests.Realtime;

/// <summary>
/// Los hechos en vivo de Crash viajan del servicio de juegos por RabbitMQ hasta el gateway y de ahi, por SignalR, a TODOS los jugadores
/// conectados (no solo al dueño de una cuenta). El navegador dibuja la ronda con esto.
/// </summary>
[Collection(WalletDbDefinition.Name)]
public sealed class CrashRealtimeTests(PostgresFixture db, RabbitMqFixture rabbit) : IAsyncLifetime
{
    private sealed class FixedSeeds : ICrashSeedSource
    {
        public string NextSeed(Guid roundId, int edgePermille)
        {
            while (true)
            {
                var seed = ProvablyFair.GenerateServerSeed();
                if (CrashMath.CrashPoint(seed, roundId, edgePermille) is >= 150 and <= 200)
                {
                    return seed;
                }
            }
        }
    }

    private CasinoCluster _cluster = null!;
    private readonly List<HubConnection> _connections = [];

    public Task InitializeAsync()
    {
        _cluster = TestAuth.StartApp(db, rabbit, customize: builder =>
        {
            builder.UseSetting("Crash:BettingSeconds", "1");
            builder.UseSetting("Crash:PauseSeconds", "0");
            builder.UseSetting("Crash:GrowthPerSecond", "1");
            builder.ConfigureTestServices(services => services.AddSingleton<ICrashSeedSource>(new FixedSeeds()));
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }

        _cluster.Dispose();
    }

    private HubConnection Connect(string token)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_cluster.Server.BaseAddress, "/hubs/player"), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.HttpMessageHandlerFactory = _ => _cluster.Server.CreateHandler();
                options.WebSocketFactory = (context, ct) =>
                {
                    var uri = new Uri($"{context.Uri}{(string.IsNullOrEmpty(context.Uri.Query) ? "?" : "&")}access_token={Uri.EscapeDataString(token)}");
                    return new ValueTask<System.Net.WebSockets.WebSocket>(_cluster.Server.CreateWebSocketClient().ConnectAsync(uri, ct));
                };
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        _connections.Add(connection);
        return connection;
    }

    private static List<JsonElement> Listen(HubConnection connection)
    {
        var events = new List<JsonElement>();
        connection.On<JsonElement>("gameEvent", notice => { lock (events) { events.Add(notice); } });
        return events;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"No se cumplio a tiempo: {what}");
    }

    [Fact]
    public async Task Every_connected_player_receives_the_phases_of_the_round_and_the_revealed_seed_matches_the_commitment()
    {
        var first = Connect(TestAuth.Token(Guid.NewGuid()));
        var second = Connect(TestAuth.Token(Guid.NewGuid()));
        var firstEvents = Listen(first);
        var secondEvents = Listen(second);
        await first.StartAsync();
        await second.StartAsync();

        await _cluster.Games.Services.GetRequiredService<CrashEngine>().RunOneRoundAsync(CancellationToken.None);

        foreach (var events in new[] { firstEvents, secondEvents })
        {
            await WaitUntilAsync(() => { lock (events) { return events.Any(e => e.GetProperty("kind").GetString() == "roundCrashed"); } }, "el aviso de explosion");
            JsonElement[] crash;
            lock (events)
            {
                crash = [.. events.Where(e => e.GetProperty("game").GetString() == "crash")];
            }

            // Se avisa en orden: se abrio, empezo a subir, exploto.
            Assert.Equal(["roundOpened", "roundStarted", "roundCrashed"], crash.Select(e => e.GetProperty("kind").GetString()));
            var opened = crash[0].GetProperty("data");
            var crashed = crash[2].GetProperty("data");
            Assert.Equal(opened.GetProperty("roundId").GetGuid(), crashed.GetProperty("roundId").GetGuid());
            Assert.Equal(1.0, crash[1].GetProperty("data").GetProperty("growthPerSecond").GetDouble());

            // Lo comprometido ANTES de apostar coincide con lo revelado al explotar.
            Assert.True(ProvablyFair.MatchesCommitment(crashed.GetProperty("seed").GetString()!, opened.GetProperty("commitment").GetString()!));
            Assert.InRange(crashed.GetProperty("crashPoint").GetInt64(), 150, 200);
            Assert.True(opened.GetProperty("bettingEndsAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));
        }
    }

    [Fact]
    public async Task The_phase_notices_carry_no_personal_data_and_do_not_reveal_the_seed_before_the_crash()
    {
        var connection = Connect(TestAuth.Token(Guid.NewGuid()));
        var events = Listen(connection);
        await connection.StartAsync();

        await _cluster.Games.Services.GetRequiredService<CrashEngine>().RunOneRoundAsync(CancellationToken.None);
        await WaitUntilAsync(() => { lock (events) { return events.Count >= 3; } }, "los tres avisos");

        lock (events)
        {
            foreach (var notice in events.Where(e => e.GetProperty("kind").GetString() is "roundOpened" or "roundStarted"))
            {
                var text = notice.GetRawText();
                Assert.DoesNotContain("seed\"", text, StringComparison.OrdinalIgnoreCase); // la semilla solo viaja en "roundCrashed"
                Assert.DoesNotContain("crashPoint", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("userId", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("accountId", text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
