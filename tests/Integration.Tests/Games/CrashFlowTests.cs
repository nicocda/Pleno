using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casino.Integration.Tests.Wallet;
using Casino.Modules.Games.Application;
using Casino.Modules.Games.Crash;
using Casino.Modules.Games.Fairness;
using Casino.Modules.Wallet.Application;
using Marten;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Casino.Integration.Tests.Games;

/// <summary>
/// Crash de punta a punta con Postgres y RabbitMQ reales: la ronda compartida, las apuestas que retiran o explotan, el retiro automatico,
/// el compromiso provably fair y el saldo que siempre cierra. Las rondas se corren de a una y con semillas elegidas para saber donde explotan.
/// </summary>
[Collection(WalletDbDefinition.Name)]
public sealed class CrashFlowTests(PostgresFixture db, RabbitMqFixture rabbit) : IDisposable
{
    /// <summary>Una fuente de semillas que, para cada ronda, busca una semilla cuyo punto de explosion cae en el rango pedido.</summary>
    private sealed class TargetedSeeds : ICrashSeedSource
    {
        private readonly Queue<(long Min, long Max)> _targets = new();

        public void Target(long min, long max)
        {
            lock (_targets)
            {
                _targets.Enqueue((min, max));
            }
        }

        public string NextSeed(Guid roundId, int edgePermille)
        {
            (long Min, long Max) target;
            lock (_targets)
            {
                target = _targets.Count > 0 ? _targets.Dequeue() : (200, 300);
            }

            while (true)
            {
                var seed = ProvablyFair.GenerateServerSeed();
                var point = CrashMath.CrashPoint(seed, roundId, edgePermille);
                if (point >= target.Min && point <= target.Max)
                {
                    return seed;
                }
            }
        }
    }

    private readonly List<CasinoCluster> _clusters = [];
    private readonly TargetedSeeds _seeds = new();

    public void Dispose()
    {
        foreach (var cluster in _clusters)
        {
            cluster.Dispose();
        }
    }

    private CasinoCluster Start()
    {
        var cluster = TestAuth.StartApp(db, rabbit, customize: builder =>
        {
            builder.UseSetting("Crash:BettingSeconds", "4");
            builder.UseSetting("Crash:PauseSeconds", "0");
            builder.UseSetting("Crash:GrowthPerSecond", "1");
            builder.UseSetting("Crash:MaxStake", "100000");
            builder.ConfigureTestServices(services => services.AddSingleton<ICrashSeedSource>(_seeds));
        });
        _clusters.Add(cluster);
        return cluster;
    }

    private static CrashEngine Engine(CasinoCluster cluster) => cluster.Games.Services.GetRequiredService<CrashEngine>();

    private static CrashService Crash(CasinoCluster cluster) => cluster.Games.Services.GetRequiredService<CrashService>();

    private static async Task<(Guid UserId, Guid AccountId)> FundedAsync(CasinoCluster cluster, long chips)
    {
        var userId = Guid.NewGuid();
        var wallet = cluster.Wallet.Services.GetRequiredService<WalletService>();
        var accountId = await wallet.OpenAccountAsync(userId);
        await wallet.CreditAsync(accountId, "initial-credit", chips);
        return (userId, accountId);
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

    private static async Task<JsonElement> StateAsync(HttpClient client) => await client.GetFromJsonAsync<JsonElement>("/games/crash/state");

    private static Task WaitForPhaseAsync(HttpClient client, string phase) =>
        WaitUntilAsync(async () => (await StateAsync(client)).TryGetProperty("round", out var round) && round.ValueKind == JsonValueKind.Object && round.GetProperty("phase").GetString() == phase, $"la ronda en {phase}");

    private static Task<JsonElement> BetAsync(HttpClient client, Guid betId) => client.GetFromJsonAsync<JsonElement>($"/games/crash/bets/{betId}");

    private static Task WaitForBetAsync(HttpClient client, Guid betId, Func<JsonElement, bool> condition, string what) =>
        WaitUntilAsync(async () => condition(await BetAsync(client, betId)), what);

    private static Task<HttpResponseMessage> PlaceAsync(HttpClient client, long stake, string key, decimal? autoCashOut = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/games/crash/bets") { Content = JsonContent.Create(new { stake, autoCashOut }) };
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    private static async Task<Guid> PlacedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("betId").GetGuid();
    }

    /// <summary>Corre una ronda del motor en segundo plano, con el resultado ya elegido.</summary>
    private Task StartRound(CasinoCluster cluster, long crashMin, long crashMax, CancellationToken ct = default)
    {
        _seeds.Target(crashMin, crashMax);
        return Task.Run(() => Engine(cluster).RunOneRoundAsync(ct), ct);
    }

    [Fact]
    public async Task A_player_who_cashes_out_in_time_is_paid_at_the_multiplier_the_server_saw_and_the_balance_closes()
    {
        using var cluster = Start();
        var (userId, accountId) = await FundedAsync(cluster, 1_000);
        using var client = cluster.ClientFor(userId);

        var round = StartRound(cluster, 800, 900);
        await WaitForPhaseAsync(client, "Betting");
        var betId = await PlacedAsync(await PlaceAsync(client, 100, "cash-1"));
        await WaitForBetAsync(client, betId, b => b.GetProperty("inPlay").GetBoolean(), "la apuesta reservada y en juego");
        await WaitForPhaseAsync(client, "Running");

        var response = await client.PostAsync($"/games/crash/bets/{betId}/cashout", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cashed = await response.Content.ReadFromJsonAsync<JsonElement>();
        var multiplier = cashed.GetProperty("multiplier").GetInt64();
        var payout = cashed.GetProperty("payout").GetInt64();
        Assert.InRange(multiplier, 100, 799); // el cohete todavia no habia explotado (iba a explotar entre x8 y x9)
        Assert.Equal(100 * multiplier / 100, payout);

        await round;
        await WaitForBetAsync(client, betId, b => b.GetProperty("status").GetString() == "Settled", "la apuesta cobrada");
        var bet = await BetAsync(client, betId);
        Assert.Equal(multiplier, bet.GetProperty("cashedOutAt").GetInt64());
        var account = await cluster.Wallet.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        Assert.Equal((1_000 - 100 + payout, 0L), (account.Available, account.Reserved));
    }

    [Fact]
    public async Task A_bet_that_does_not_cash_out_is_lost_and_the_round_then_reveals_a_seed_that_matches_the_commitment()
    {
        using var cluster = Start();
        var (userId, accountId) = await FundedAsync(cluster, 1_000);
        using var client = cluster.ClientFor(userId);

        var round = StartRound(cluster, 150, 200);
        await WaitForPhaseAsync(client, "Betting");
        var betting = (await StateAsync(client)).GetProperty("round");
        var roundId = betting.GetProperty("id").GetGuid();
        var commitment = betting.GetProperty("commitment").GetString()!;
        // Mientras se apuesta, ni el punto de explosion ni la semilla se pueden conocer.
        Assert.Equal(JsonValueKind.Null, betting.GetProperty("crashPoint").ValueKind);
        Assert.Equal(JsonValueKind.Null, betting.GetProperty("serverSeed").ValueKind);
        var betId = await PlacedAsync(await PlaceAsync(client, 100, "lose-1"));

        await round;
        await WaitForBetAsync(client, betId, b => b.GetProperty("status").GetString() == "Settled", "la apuesta perdida cerrada");

        var bet = await BetAsync(client, betId);
        Assert.Equal(0, bet.GetProperty("payout").GetInt64());
        var account = await cluster.Wallet.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        Assert.Equal((900L, 0L), (account.Available, account.Reserved));

        // Verificacion provably fair: la semilla revelada corresponde al compromiso publicado ANTES de apostar y da el punto de explosion.
        var revealed = await client.GetFromJsonAsync<JsonElement>($"/games/crash/rounds/{roundId}");
        var seed = revealed.GetProperty("serverSeed").GetString()!;
        var crashPoint = revealed.GetProperty("crashPoint").GetInt64();
        Assert.Equal("Crashed", revealed.GetProperty("phase").GetString());
        Assert.InRange(crashPoint, 150, 200);
        Assert.True(ProvablyFair.MatchesCommitment(seed, commitment));
        Assert.Equal(crashPoint, CrashMath.CrashPoint(seed, roundId, revealed.GetProperty("edgePermille").GetInt32()));
    }

    [Fact]
    public async Task The_automatic_cash_out_pays_exactly_at_the_requested_multiplier_when_the_rocket_gets_there_and_loses_when_it_does_not()
    {
        using var cluster = Start();
        var (userId, accountId) = await FundedAsync(cluster, 1_000);
        using var client = cluster.ClientFor(userId);

        var round = StartRound(cluster, 300, 320);
        await WaitForPhaseAsync(client, "Betting");
        var low = await PlacedAsync(await PlaceAsync(client, 100, "auto-low", 1.50m)); // x1,50 < explosion: gana
        var high = await PlacedAsync(await PlaceAsync(client, 100, "auto-high", 5.00m)); // x5,00 > explosion: pierde

        await round;
        await WaitForBetAsync(client, low, b => b.GetProperty("status").GetString() == "Settled", "el retiro automatico cobrado");
        await WaitForBetAsync(client, high, b => b.GetProperty("status").GetString() == "Settled", "la apuesta que no llego, cerrada");

        var winner = await BetAsync(client, low);
        Assert.Equal((150L, 150L), (winner.GetProperty("cashedOutAt").GetInt64(), winner.GetProperty("payout").GetInt64()));
        Assert.Equal(0, (await BetAsync(client, high)).GetProperty("payout").GetInt64());
        var account = await cluster.Wallet.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        Assert.Equal((1_000 - 200 + 150L, 0L), (account.Available, account.Reserved));
    }

    [Fact]
    public async Task A_player_who_leaves_mid_round_is_paid_by_the_automatic_cash_out_but_loses_the_bet_without_one()
    {
        using var cluster = Start();
        var (userId, accountId) = await FundedAsync(cluster, 1_000);

        var round = StartRound(cluster, 300, 320);
        Guid withAuto, withoutAuto;
        using (var client = cluster.ClientFor(userId))
        {
            await WaitForPhaseAsync(client, "Betting");
            withAuto = await PlacedAsync(await PlaceAsync(client, 100, "left-auto", 1.50m));
            withoutAuto = await PlacedAsync(await PlaceAsync(client, 100, "left-manual"));
            await WaitForBetAsync(client, withoutAuto, b => b.GetProperty("inPlay").GetBoolean(), "las apuestas en juego");
        } // se va al lobby con el cohete por despegar: nadie vuelve a consultar ni a retirar

        await round;

        // El backend resolvio solo: el retiro automatico cobro (x1,50 sobre 100) y la apuesta sin retiro se perdio, porque en Crash retirar es accion del jugador.
        using var back = cluster.ClientFor(userId);
        await WaitForBetAsync(back, withAuto, b => b.GetProperty("status").GetString() == "Settled", "el retiro automatico cobrado");
        await WaitForBetAsync(back, withoutAuto, b => b.GetProperty("status").GetString() == "Settled", "la apuesta sin retiro cerrada");
        Assert.Equal(150, (await BetAsync(back, withAuto)).GetProperty("payout").GetInt64());
        Assert.Equal(0, (await BetAsync(back, withoutAuto)).GetProperty("payout").GetInt64());
        var account = await cluster.Wallet.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        Assert.Equal((1_000 - 200 + 150L, 0L), (account.Available, account.Reserved));
    }

    private static readonly int[] ManualPlayers = [8, 9];

    [Fact]
    public async Task Ten_players_betting_and_cashing_out_at_the_same_time_share_one_round_and_every_balance_closes()
    {
        using var cluster = Start();
        const long stake = 100;
        // 6 con retiro automatico que llega, 2 con retiro automatico que no llega (x90 > explosion), 2 que retiran a mano a la vez.
        decimal?[] autos = [1.20m, 1.50m, 1.80m, 2.10m, 2.40m, 2.70m, 90m, 90m, null, null];
        var players = new List<(Guid UserId, Guid AccountId, HttpClient Client)>();
        foreach (var _ in autos)
        {
            var (userId, accountId) = await FundedAsync(cluster, 1_000);
            players.Add((userId, accountId, cluster.ClientFor(userId)));
        }

        try
        {
            var round = StartRound(cluster, 3_000, 4_000);
            await WaitForPhaseAsync(players[0].Client, "Betting");
            var roundId = (await StateAsync(players[0].Client)).GetProperty("round").GetProperty("id").GetGuid();

            // Todos apuestan a la vez, en la misma ventana.
            var betIds = await Task.WhenAll(players.Select((player, i) => Task.Run(async () => await PlacedAsync(await PlaceAsync(player.Client, stake, $"many-{i}", autos[i])))));
            for (var i = 0; i < players.Count; i++)
            {
                await WaitForBetAsync(players[i].Client, betIds[i], b => b.GetProperty("inPlay").GetBoolean() || b.GetProperty("status").GetString() != "Placed", $"la apuesta {i} reservada");
                Assert.Equal(roundId, (await BetAsync(players[i].Client, betIds[i])).GetProperty("roundId").GetGuid()); // todos en la MISMA ronda
            }

            await WaitForPhaseAsync(players[0].Client, "Running");

            // Los dos manuales retiran a la vez, mientras los retiros automaticos tambien se disparan.
            var manual = await Task.WhenAll(ManualPlayers.Select(i => Task.Run(async () => await players[i].Client.PostAsync($"/games/crash/bets/{betIds[i]}/cashout", content: null))));
            Assert.All(manual, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            var manualPayouts = new List<long>();
            foreach (var response in manual)
            {
                var cashed = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.InRange(cashed.GetProperty("multiplier").GetInt64(), 100, 2_999);
                manualPayouts.Add(cashed.GetProperty("payout").GetInt64());
            }

            await round;
            for (var i = 0; i < players.Count; i++)
            {
                await WaitForBetAsync(players[i].Client, betIds[i], b => b.GetProperty("status").GetString() == "Settled", $"la apuesta {i} cerrada");
            }

            for (var i = 0; i < players.Count; i++)
            {
                var bet = await BetAsync(players[i].Client, betIds[i]);
                var expectedPayout = i switch
                {
                    < 6 => stake * (long)(autos[i]!.Value * 100) / 100, // cobra exacto en su multiplicador
                    < 8 => 0L, // el cohete exploto antes de x90
                    _ => manualPayouts[i - 8],
                };
                Assert.Equal(expectedPayout, bet.GetProperty("payout").GetInt64());
                var account = await cluster.Wallet.Services.GetRequiredService<WalletService>().GetAsync(players[i].AccountId);
                Assert.Equal((1_000 - stake + expectedPayout, 0L), (account.Available, account.Reserved)); // cada saldo cierra, sin reservas colgadas
            }
        }
        finally
        {
            foreach (var player in players)
            {
                player.Client.Dispose();
            }
        }
    }

    [Fact]
    public async Task Betting_is_refused_when_no_round_is_open_or_the_window_already_closed()
    {
        using var cluster = Start();
        var (userId, accountId) = await FundedAsync(cluster, 1_000);
        using var client = cluster.ClientFor(userId);

        // Sin ninguna ronda abierta.
        Assert.Equal(HttpStatusCode.Conflict, (await PlaceAsync(client, 100, "none-1")).StatusCode);

        var round = StartRound(cluster, 900, 1_000);
        await WaitForPhaseAsync(client, "Running"); // ya paso la ventana de apuestas
        Assert.Equal(HttpStatusCode.Conflict, (await PlaceAsync(client, 100, "late-1")).StatusCode);

        var account = await cluster.Wallet.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        Assert.Equal((1_000L, 0L), (account.Available, account.Reserved)); // no se reservo nada
        await Crash(cluster).CrashRoundAsync((await StateAsync(client)).GetProperty("round").GetProperty("id").GetGuid());
        await round;
    }

    [Fact]
    public async Task A_bet_without_enough_chips_is_rejected_and_takes_nothing()
    {
        using var cluster = Start();
        var (userId, accountId) = await FundedAsync(cluster, 50);
        using var client = cluster.ClientFor(userId);

        var round = StartRound(cluster, 100, 120);
        await WaitForPhaseAsync(client, "Betting");
        var betId = await PlacedAsync(await PlaceAsync(client, 100, "poor-1"));

        await WaitForBetAsync(client, betId, b => b.GetProperty("status").GetString() == "Rejected", "la apuesta rechazada");
        await round;
        var account = await cluster.Wallet.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        Assert.Equal((50L, 0L), (account.Available, account.Reserved));
        Assert.Equal("InsufficientFunds", (await BetAsync(client, betId)).GetProperty("failureReason").GetString());
    }

    [Fact]
    public async Task Repeating_a_bet_with_the_same_key_places_it_once_and_other_content_with_that_key_is_a_conflict()
    {
        using var cluster = Start();
        var (userId, accountId) = await FundedAsync(cluster, 1_000);
        using var client = cluster.ClientFor(userId);

        var round = StartRound(cluster, 200, 250);
        await WaitForPhaseAsync(client, "Betting");
        var first = await PlacedAsync(await PlaceAsync(client, 100, "same-key"));
        var again = await PlaceAsync(client, 100, "same-key");

        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        var repeated = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(first, repeated.GetProperty("betId").GetGuid());
        Assert.True(repeated.GetProperty("alreadyPlaced").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await PlaceAsync(client, 999, "same-key")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PlaceAsync(client, 100, "same-key", 2.00m)).StatusCode);

        await round;
        await WaitForBetAsync(client, first, b => b.GetProperty("status").GetString() == "Settled", "la apuesta cerrada");
        var account = await cluster.Wallet.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        Assert.True(account.Available is 900 or > 900); // una sola vez: nunca -200
        Assert.Equal(0L, account.Reserved);
    }

    [Fact]
    public async Task Invalid_bets_are_refused_before_anything_is_reserved()
    {
        using var cluster = Start();
        var (userId, accountId) = await FundedAsync(cluster, 1_000);
        using var client = cluster.ClientFor(userId);

        Assert.Equal(HttpStatusCode.BadRequest, (await PlaceAsync(client, 0, "v0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PlaceAsync(client, -5, "v1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PlaceAsync(client, 100_001, "v2")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PlaceAsync(client, 10, "v3", 1.00m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PlaceAsync(client, 10, "v4", 0.50m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PlaceAsync(client, 10, "v5", 1_000.01m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PlaceAsync(client, 10, string.Empty)).StatusCode);

        var account = await cluster.Wallet.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        Assert.Equal((1_000L, 0L), (account.Available, account.Reserved));
    }

    [Fact]
    public async Task Only_the_owner_can_cash_out_and_only_once_while_the_rocket_is_running()
    {
        using var cluster = Start();
        var (userId, _) = await FundedAsync(cluster, 1_000);
        var (otherId, _) = await FundedAsync(cluster, 1_000);
        using var client = cluster.ClientFor(userId);
        using var other = cluster.ClientFor(otherId);

        var round = StartRound(cluster, 900, 1_000);
        await WaitForPhaseAsync(client, "Betting");
        var betId = await PlacedAsync(await PlaceAsync(client, 100, "guard-1"));
        await WaitForBetAsync(client, betId, b => b.GetProperty("inPlay").GetBoolean(), "la apuesta en juego");

        // Antes de que empiece a subir no hay nada que retirar.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/games/crash/bets/{betId}/cashout", null)).StatusCode);
        await WaitForPhaseAsync(client, "Running");
        // Un jugador ajeno no ve ni retira la apuesta de otro.
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/games/crash/bets/{betId}/cashout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/games/crash/bets/{betId}")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/games/crash/bets/{betId}/cashout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/games/crash/bets/{betId}/cashout", null)).StatusCode); // ya retiro
        await Crash(cluster).CrashRoundAsync((await StateAsync(client)).GetProperty("round").GetProperty("id").GetGuid());
        await round;
    }

    [Fact]
    public async Task A_cash_out_that_arrives_after_the_crash_is_refused_and_the_bet_is_lost()
    {
        using var cluster = Start();
        var (userId, accountId) = await FundedAsync(cluster, 1_000);
        using var client = cluster.ClientFor(userId);

        var round = StartRound(cluster, 100, 100); // explota apenas empieza
        await WaitForPhaseAsync(client, "Betting");
        var betId = await PlacedAsync(await PlaceAsync(client, 100, "late-cash-1"));
        await round;

        var response = await client.PostAsync($"/games/crash/bets/{betId}/cashout", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await WaitForBetAsync(client, betId, b => b.GetProperty("status").GetString() == "Settled", "la apuesta perdida cerrada");
        var account = await cluster.Wallet.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        Assert.Equal(900L, account.Available);
    }

    [Fact]
    public async Task A_round_cut_short_by_a_restart_gives_every_stake_back()
    {
        using var cluster = Start();
        var (userId, accountId) = await FundedAsync(cluster, 1_000);
        using var client = cluster.ClientFor(userId);
        using var stop = new CancellationTokenSource();

        var round = StartRound(cluster, 900, 1_000, stop.Token); // una ronda larga
        await WaitForPhaseAsync(client, "Betting");
        var betId = await PlacedAsync(await PlaceAsync(client, 100, "abort-1"));
        await WaitForBetAsync(client, betId, b => b.GetProperty("inPlay").GetBoolean(), "la apuesta en juego");
        await WaitForPhaseAsync(client, "Running");

        await Crash(cluster).RecoverAsync(); // como si el servicio se reiniciara en medio de la ronda

        await WaitForBetAsync(client, betId, b => b.GetProperty("status").GetString() == "Settled", "la apuesta devuelta");
        var bet = await BetAsync(client, betId);
        Assert.Equal((100L, "RoundAborted"), (bet.GetProperty("payout").GetInt64(), bet.GetProperty("failureReason").GetString()));
        var account = await cluster.Wallet.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        Assert.Equal((1_000L, 0L), (account.Available, account.Reserved)); // nadie pierde por una caida
        stop.Cancel();
        await Task.WhenAny(round, Task.Delay(2_000));
    }

    [Fact]
    public async Task A_reservation_that_arrives_after_the_betting_window_gets_the_stake_back_instead_of_playing()
    {
        using var cluster = Start();
        var (userId, accountId) = await FundedAsync(cluster, 1_000);
        var crash = Crash(cluster);
        _seeds.Target(900, 1_000);
        var open = await crash.OpenRoundAsync();
        var started = await crash.StartRoundAsync(open.Id); // la ventana ya se cerro
        var betId = Guid.NewGuid();
        await using (var session = cluster.Games.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Insert(new CrashBet { Id = betId, RoundId = open.Id, UserId = userId, AccountId = accountId, Stake = 100, Status = Casino.Modules.Games.Application.RoundStatus.Placed, PlacedAt = DateTimeOffset.UtcNow });
            await session.SaveChangesAsync();
        }

        await crash.OnStakeReservedAsync(new Casino.Contracts.StakeReserved(betId, accountId, 100, Guid.NewGuid()));

        var bet = await crash.GetBetAsync(betId);
        Assert.Equal((Casino.Modules.Games.Application.RoundStatus.Resolved, 100L, "BettingClosed"), (bet.Status, bet.Payout, bet.FailureReason));
        Assert.Equal(CrashPhase.Running, started.Phase);
        await crash.CrashRoundAsync(open.Id);
    }

    [Fact]
    public async Task The_state_shows_the_current_round_my_bet_and_the_last_crashes_without_leaking_the_seed_before_time()
    {
        using var cluster = Start();
        var (userId, _) = await FundedAsync(cluster, 1_000);
        using var client = cluster.ClientFor(userId);

        await StartRound(cluster, 200, 250);
        var after = await StateAsync(client);

        Assert.Equal(1, after.GetProperty("minStake").GetInt64());
        Assert.Equal(100_000, after.GetProperty("maxStake").GetInt64());
        Assert.Equal(1.0, after.GetProperty("growthPerSecond").GetDouble());
        Assert.Equal("Crashed", after.GetProperty("round").GetProperty("phase").GetString());
        Assert.Contains(after.GetProperty("history").EnumerateArray(), r => r.GetProperty("crashPoint").GetInt64() is >= 200 and <= 250);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("myBet").ValueKind);
        using var anonymous = cluster.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/games/crash/state")).StatusCode);
    }

    [Fact]
    public async Task The_wallet_reservation_of_a_crash_bet_lasts_through_the_longest_possible_round()
    {
        // La Wallet libera sola toda reserva que no se liquida a tiempo; una ronda de Crash puede durar ~100 s. Con un plazo por defecto muy corto
        // (1 s), la apuesta igual sobrevive porque Crash pide un plazo propio en la orden de reserva.
        using var cluster = TestAuth.StartApp(db, rabbit, customize: builder =>
        {
            builder.UseSetting("Wallet:ReservationTtlSeconds", "1");
            builder.UseSetting("Crash:BettingSeconds", "4");
            builder.UseSetting("Crash:PauseSeconds", "0");
            builder.UseSetting("Crash:GrowthPerSecond", "0.3");
            builder.ConfigureTestServices(services => services.AddSingleton<ICrashSeedSource>(_seeds));
        });
        _clusters.Add(cluster);
        var (userId, accountId) = await FundedAsync(cluster, 1_000);
        using var client = cluster.ClientFor(userId);
        using var stop = new CancellationTokenSource();

        var round = StartRound(cluster, 900, 1_000, stop.Token);
        await WaitForPhaseAsync(client, "Betting");
        var betId = await PlacedAsync(await PlaceAsync(client, 100, "ttl-1"));
        await WaitForBetAsync(client, betId, b => b.GetProperty("inPlay").GetBoolean(), "la apuesta en juego");
        await Task.Delay(4_000); // mas que el plazo por defecto de la Wallet

        var account = await cluster.Wallet.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        Assert.Equal((900L, 100L), (account.Available, account.Reserved)); // sigue reservada: no se libero sola
        Assert.True((await BetAsync(client, betId)).GetProperty("inPlay").GetBoolean());
        stop.Cancel();
        await Crash(cluster).RecoverAsync();
        await Task.WhenAny(round, Task.Delay(2_000));
    }
}
