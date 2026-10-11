using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casino.BuildingBlocks;
using Casino.Contracts;
using Casino.Integration.Tests.Wallet;
using Casino.Modules.Games.Application;
using Casino.Modules.Games.Fairness;
using Casino.Modules.Games.Roulette;
using Casino.Modules.Wallet.Application;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace Casino.Integration.Tests.Games;

/// <summary>
/// El juego completo de punta a punta, con Postgres y RabbitMQ reales y un jugador autenticado:
/// API -> (Wallet reserva) -> juego sortea con el nonce del servidor -> (Wallet liquida) -> ronda cerrada.
/// </summary>
[Collection(WalletDbDefinition.Name)]
public sealed class RouletteFlowTests(PostgresFixture db, RabbitMqFixture rabbit, ITestOutputHelper output) : IDisposable
{
    private readonly List<CasinoCluster> _apps = [];

    public void Dispose()
    {
        foreach (var app in _apps)
        {
            app.Dispose();
        }
    }

    /// <param name="gamesDown">Simula que el motor de juegos no esta consumiendo: nadie sortea ni responde.</param>
    private CasinoCluster StartApp(int ttlSeconds = 60, bool gamesDown = false)
    {
        var app = TestAuth.StartApp(db, rabbit, customize: builder =>
        {
            builder.UseSetting("Wallet:ReservationTtlSeconds", ttlSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (gamesDown)
            {
                builder.ConfigureServices(services => services.ConfigureWolverine(options =>
                    options.Discovery.CustomizeHandlerDiscovery(query =>
                        query.Excludes.WithCondition("games down", type => type == typeof(WalletEventsHandler)))));
            }
        });
        _apps.Add(app);
        return app;
    }

    /// <summary>Un jugador con la cuenta ya abierta y fondeada (sin fichas de bienvenida, para que los saldos sean exactos).</summary>
    private static async Task<(Guid UserId, Guid AccountId)> FundedPlayerAsync(CasinoCluster app, long chips)
    {
        var userId = Guid.NewGuid();
        var wallet = app.Services.GetRequiredService<WalletService>();
        var accountId = await wallet.OpenAccountAsync(userId);
        await wallet.CreditAsync(accountId, "initial-credit", chips);
        return (userId, accountId);
    }

    private static async Task<HttpResponseMessage> PlaceAsync(HttpClient client, string betType, int[] selection, long stake, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/games/roulette/bets")
        {
            Content = JsonContent.Create(new { betType, selection, stake }),
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PlaceSpinAsync(HttpClient client, object[] bets, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/games/roulette/bets") { Content = JsonContent.Create(new { bets }) };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> PlacedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<ITrackedSession> TrackPublishAsync(CasinoCluster app, object message)
    {
        Func<IMessageContext, Task> publish = async context => await context.PublishAsync(message);
        // Se sigue la actividad de AMBOS servicios (los juegos y la Wallet): el mensaje cruza RabbitMQ entre ellos.
        return app.Games.Services.GetRequiredService<IHost>().TrackActivity()
            .AlsoTrack(app.Wallet.Services.GetRequiredService<IHost>())
            .IncludeExternalTransports()
            .Timeout(TimeSpan.FromSeconds(45))
            .ExecuteAndWaitAsync(publish);
    }

    private static Task<JsonElement> RoundAsync(HttpClient client, Guid betId) =>
        client.GetFromJsonAsync<JsonElement>($"/games/roulette/rounds/{betId}");

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, int seconds = 90)
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
                // Todavia no esta listo (por ejemplo, el broker se esta recuperando).
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"No se cumplio a tiempo: {what}");
    }

    private static Task WaitForStatusAsync(HttpClient client, Guid betId, string status, int seconds = 90) =>
        WaitUntilAsync(async () => (await RoundAsync(client, betId)).GetProperty("status").GetString() == status, $"ronda {betId} en {status}", seconds);

    [Fact]
    public async Task A_bet_runs_from_placement_to_settlement_and_the_player_can_verify_the_result()
    {
        using var app = StartApp();
        var (userId, accountId) = await FundedPlayerAsync(app, 1_000);
        using var client = app.ClientFor(userId);

        var placed = await PlacedAsync(await PlaceAsync(client, "Red", [], 100, "bet-1"));
        var betId = placed.GetProperty("betId").GetGuid();
        var commitment = placed.GetProperty("commitment").GetString()!;
        Assert.Equal(0, placed.GetProperty("nonce").GetInt64());

        await WaitForStatusAsync(client, betId, "Settled");

        var round = await RoundAsync(client, betId);
        var winning = round.GetProperty("winningNumber").GetInt32();
        var payout = round.GetProperty("payout").GetInt64();
        Assert.Equal(RouletteBet.Create(RouletteBetType.Red, [], 100).PayoutFor(winning), payout);

        var account = await app.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        Assert.Equal((1_000 - 100 + payout, 0L), (account.Available, account.Reserved));

        // El jugador rota para que se revele la seed y verifica el sorteo con datos publicos.
        var rotated = await (await client.PostAsync("/fairness/me/rotate", content: null)).Content.ReadFromJsonAsync<JsonElement>();
        var retired = rotated.GetProperty("retired")[0];
        var serverSeed = retired.GetProperty("serverSeed").GetString()!;
        Assert.True(ProvablyFair.MatchesCommitment(serverSeed, commitment));
        var recomputed = new FairRng(serverSeed, retired.GetProperty("clientSeed").GetString()!, round.GetProperty("nonce").GetInt64())
            .NextInt(RouletteBet.PocketCount);
        Assert.Equal(winning, recomputed);
        output.WriteLine($"Salio el {winning}; premio {payout}; verificado contra el compromiso {commitment[..16]}...");
    }

    [Fact]
    public async Task A_player_who_leaves_right_after_betting_still_gets_the_round_resolved_and_paid_by_the_backend()
    {
        using var app = StartApp();
        var (userId, accountId) = await FundedPlayerAsync(app, 1_000);
        var wallet = app.Services.GetRequiredService<WalletService>();

        // Apuesta y se va (vuelve al lobby o cierra la pestana) sin esperar el resultado.
        Guid betId;
        using (var client = app.ClientFor(userId))
        {
            betId = (await PlacedAsync(await PlaceAsync(client, "Red", [], 100, "left-1"))).GetProperty("betId").GetGuid();
        }

        await WaitUntilAsync(async () => (await wallet.GetAsync(accountId)).Reserved == 0, "las fichas liquidadas sin que el jugador este conectado");

        // Al volver, la ronda ya esta cerrada y el saldo refleja el premio (o la perdida).
        using var back = app.ClientFor(userId);
        await WaitForStatusAsync(back, betId, "Settled");
        var payout = (await RoundAsync(back, betId)).GetProperty("payout").GetInt64();
        var account = await wallet.GetAsync(accountId);
        Assert.Equal((1_000 - 100 + payout, 0L), (account.Available, account.Reserved));
    }

    [Fact]
    public async Task Insufficient_funds_rejects_the_round_and_it_never_blocks_the_seed_rotation()
    {
        using var app = StartApp();
        var (userId, accountId) = await FundedPlayerAsync(app, 50);
        using var client = app.ClientFor(userId);

        var placed = await PlacedAsync(await PlaceAsync(client, "Red", [], 500, "bet-1"));
        var betId = placed.GetProperty("betId").GetGuid();

        await WaitForStatusAsync(client, betId, "Rejected");

        var round = await RoundAsync(client, betId);
        Assert.Equal("InsufficientFunds", round.GetProperty("failureReason").GetString());
        Assert.Equal(JsonValueKind.Null, round.GetProperty("winningNumber").ValueKind);
        Assert.Equal(50, (await app.Services.GetRequiredService<WalletService>().GetAsync(accountId)).Available);

        await WaitUntilAsync(
            async () => (await client.PostAsync("/fairness/me/rotate", content: null)).StatusCode == HttpStatusCode.OK,
            "rotacion permitida tras cerrar la ronda");
    }

    [Fact]
    public async Task The_player_and_account_come_from_the_token_not_from_the_request_body()
    {
        using var app = StartApp();
        var (victimId, victimAccount) = await FundedPlayerAsync(app, 1_000);
        var (attackerId, attackerAccount) = await FundedPlayerAsync(app, 1_000);
        using var attacker = app.ClientFor(attackerId);

        // El atacante intenta apostar con la cuenta y el usuario de la victima mandandolos en el cuerpo.
        var request = new HttpRequestMessage(HttpMethod.Post, "/games/roulette/bets")
        {
            Content = JsonContent.Create(new { betType = "Red", selection = Array.Empty<int>(), stake = 100, userId = victimId, accountId = victimAccount }),
        };
        request.Headers.Add("Idempotency-Key", "steal-1");
        var placed = await PlacedAsync(await attacker.SendAsync(request));
        var betId = placed.GetProperty("betId").GetGuid();
        await WaitForStatusAsync(attacker, betId, "Settled");

        var wallet = app.Services.GetRequiredService<WalletService>();
        Assert.Equal(1_000, (await wallet.GetAsync(victimAccount)).Available); // la victima no se toco
        var payout = (await RoundAsync(attacker, betId)).GetProperty("payout").GetInt64();
        Assert.Equal(1_000 - 100 + payout, (await wallet.GetAsync(attackerAccount)).Available);
    }

    [Fact]
    public async Task A_player_cannot_see_the_rounds_of_another_player()
    {
        using var app = StartApp();
        var (ownerId, _) = await FundedPlayerAsync(app, 1_000);
        var (otherId, _) = await FundedPlayerAsync(app, 1_000);
        using var owner = app.ClientFor(ownerId);
        using var other = app.ClientFor(otherId);
        var placed = await PlacedAsync(await PlaceAsync(owner, "Red", [], 100, "bet-1"));
        var betId = placed.GetProperty("betId").GetGuid();
        await WaitForStatusAsync(owner, betId, "Settled");

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/games/roulette/rounds/{betId}")).StatusCode);

        var otherHistory = await other.GetFromJsonAsync<JsonElement>("/games/roulette/rounds");
        Assert.Equal(0, otherHistory.GetArrayLength());
        var ownerHistory = await owner.GetFromJsonAsync<JsonElement>("/games/roulette/rounds");
        Assert.Equal(betId, ownerHistory[0].GetProperty("betId").GetGuid());
    }

    [Fact]
    public async Task The_round_history_lists_the_newest_first_and_honours_the_limit()
    {
        using var app = StartApp();
        var (userId, _) = await FundedPlayerAsync(app, 1_000);
        using var client = app.ClientFor(userId);
        var betIds = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var placed = await PlacedAsync(await PlaceAsync(client, "Odd", [], 10, $"bet-{i}"));
            betIds.Add(placed.GetProperty("betId").GetGuid());
            await WaitForStatusAsync(client, betIds[^1], "Settled");
        }

        var history = await client.GetFromJsonAsync<JsonElement>("/games/roulette/rounds?limit=2");

        Assert.Equal(2, history.GetArrayLength());
        Assert.Equal(betIds[2], history[0].GetProperty("betId").GetGuid());
        Assert.Equal(betIds[1], history[1].GetProperty("betId").GetGuid());
    }

    [Fact]
    public async Task Placing_the_same_bet_twice_reserves_once_and_a_reused_key_with_other_content_is_rejected()
    {
        using var app = StartApp();
        var (userId, accountId) = await FundedPlayerAsync(app, 1_000);
        using var client = app.ClientFor(userId);

        var first = await PlacedAsync(await PlaceAsync(client, "Even", [], 100, "same-key"));
        var second = await PlacedAsync(await PlaceAsync(client, "Even", [], 100, "same-key"));

        Assert.Equal(first.GetProperty("betId").GetGuid(), second.GetProperty("betId").GetGuid());
        Assert.False(first.GetProperty("alreadyPlaced").GetBoolean());
        Assert.True(second.GetProperty("alreadyPlaced").GetBoolean());
        Assert.Equal(first.GetProperty("nonce").GetInt64(), second.GetProperty("nonce").GetInt64());

        var betId = first.GetProperty("betId").GetGuid();
        await WaitForStatusAsync(client, betId, "Settled");
        var payout = (await RoundAsync(client, betId)).GetProperty("payout").GetInt64();
        var account = await app.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        Assert.Equal(1_000 - 100 + payout, account.Available); // una sola vez: nunca -200

        var reused = await PlaceAsync(client, "Even", [], 999, "same-key");
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
    }

    [Fact]
    public async Task A_spin_with_several_bets_reserves_the_total_draws_once_and_pays_the_sum()
    {
        using var app = StartApp();
        var (userId, accountId) = await FundedPlayerAsync(app, 1_000);
        using var client = app.ClientFor(userId);

        // Rojo Y negro a la vez es legal (una de las dos pierde siempre), igual que un pleno y su columna.
        var placed = await PlacedAsync(await PlaceSpinAsync(
            client,
            [
                new { betType = "Red", selection = Array.Empty<int>(), stake = 100L },
                new { betType = "Black", selection = Array.Empty<int>(), stake = 50L },
                new { betType = "Straight", selection = new List<int> { 7 }, stake = 10L },
                new { betType = "Column", selection = new List<int> { 1 }, stake = 40L },
            ],
            "spin-1"));
        var betId = placed.GetProperty("betId").GetGuid();

        await WaitForStatusAsync(client, betId, "Settled");

        var round = await RoundAsync(client, betId);
        var winning = round.GetProperty("winningNumber").GetInt32();
        var expected =
            RouletteBet.Create(RouletteBetType.Red, [], 100).PayoutFor(winning)
            + RouletteBet.Create(RouletteBetType.Black, [], 50).PayoutFor(winning)
            + RouletteBet.Create(RouletteBetType.Straight, [7], 10).PayoutFor(winning)
            + RouletteBet.Create(RouletteBetType.Column, [1], 40).PayoutFor(winning);
        Assert.Equal(200, round.GetProperty("stake").GetInt64()); // el total apostado
        Assert.Equal(4, round.GetProperty("bets").GetArrayLength());
        Assert.Equal(expected, round.GetProperty("payout").GetInt64());

        // Una sola reserva por el total y una sola liquidacion: el saldo cierra exacto.
        var account = await app.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        Assert.Equal((1_000 - 200 + expected, 0L), (account.Available, account.Reserved));
    }

    [Fact]
    public async Task A_spin_is_rejected_whole_when_the_total_exceeds_the_balance()
    {
        using var app = StartApp();
        var (userId, accountId) = await FundedPlayerAsync(app, 100);
        using var client = app.ClientFor(userId);

        var placed = await PlacedAsync(await PlaceSpinAsync(
            client,
            [new { betType = "Red", selection = Array.Empty<int>(), stake = 60L }, new { betType = "Odd", selection = Array.Empty<int>(), stake = 60L }],
            "spin-big"));

        await WaitForStatusAsync(client, placed.GetProperty("betId").GetGuid(), "Rejected");
        var account = await app.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        Assert.Equal((100L, 0L), (account.Available, account.Reserved)); // ninguna de las dos se jugo
    }

    [Fact]
    public async Task Retrying_a_spin_in_another_order_is_the_same_spin_but_other_content_is_a_conflict()
    {
        using var app = StartApp();
        var (userId, _) = await FundedPlayerAsync(app, 1_000);
        using var client = app.ClientFor(userId);
        var red = new { betType = "Red", selection = Array.Empty<int>(), stake = 10L };
        var seven = new { betType = "Straight", selection = new[] { 7 }, stake = 10L };

        var first = await PlacedAsync(await PlaceSpinAsync(client, [red, seven], "spin-retry"));
        var again = await PlacedAsync(await PlaceSpinAsync(client, [seven, red], "spin-retry"));

        Assert.Equal(first.GetProperty("betId").GetGuid(), again.GetProperty("betId").GetGuid());
        Assert.True(again.GetProperty("alreadyPlaced").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await PlaceSpinAsync(client, [red], "spin-retry")).StatusCode);
    }

    [Fact]
    public async Task A_spin_with_an_invalid_bet_inside_is_rejected_before_anything_is_reserved()
    {
        using var app = StartApp();
        var (userId, accountId) = await FundedPlayerAsync(app, 1_000);
        using var client = app.ClientFor(userId);

        var response = await PlaceSpinAsync(
            client,
            [new { betType = "Red", selection = Array.Empty<int>(), stake = 10L }, new { betType = "Straight", selection = new List<int> { 99 }, stake = 10L }],
            "spin-bad");
        var empty = await PlaceSpinAsync(client, [], "spin-empty");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        var account = await app.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        Assert.Equal((1_000L, 0L), (account.Available, account.Reserved));
    }

    [Fact]
    public async Task Invalid_bets_are_rejected_before_anything_is_reserved()
    {
        using var app = StartApp();
        var (userId, accountId) = await FundedPlayerAsync(app, 1_000);
        using var client = app.ClientFor(userId);

        Assert.Equal(HttpStatusCode.BadRequest, (await PlaceAsync(client, "Straight", [40], 10, "k1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PlaceAsync(client, "Split", [1, 5], 10, "k2")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PlaceAsync(client, "Red", [], 0, "k3")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PlaceAsync(client, "Inventada", [], 10, "k4")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PlaceAsync(client, "Red", [], 10, string.Empty)).StatusCode);

        var account = await app.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        Assert.Equal((1_000L, 0L), (account.Available, account.Reserved));
    }

    [Fact]
    public async Task A_duplicated_stake_reserved_message_does_not_draw_a_second_time()
    {
        using var app = StartApp();
        var (userId, accountId) = await FundedPlayerAsync(app, 1_000);
        using var client = app.ClientFor(userId);
        var placed = await PlacedAsync(await PlaceAsync(client, "Black", [], 100, "bet-1"));
        var betId = placed.GetProperty("betId").GetGuid();
        await WaitForStatusAsync(client, betId, "Settled");
        var before = await RoundAsync(client, betId);

        var session = await TrackPublishAsync(app, new StakeReserved(betId, accountId, 100, Guid.NewGuid()));

        Assert.DoesNotContain(session.Sent.MessagesOf<RoundResolved>(), m => m.BetId == betId);
        var after = await RoundAsync(client, betId);
        Assert.Equal(before.GetProperty("winningNumber").GetInt32(), after.GetProperty("winningNumber").GetInt32());
        Assert.Equal("Settled", after.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Forty_concurrent_bets_get_contiguous_server_assigned_nonces_and_every_balance_closes()
    {
        using var app = StartApp();
        var (userId, accountId) = await FundedPlayerAsync(app, 10_000);
        using var client = app.ClientFor(userId);

        var placements = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(async () =>
            await PlacedAsync(await PlaceAsync(client, "Straight", [17], 10, $"bet-{i}")))));

        var betIds = placements.Select(p => p.GetProperty("betId").GetGuid()).ToList();
        Assert.Equal(Enumerable.Range(0, 40).Select(n => (long)n), placements.Select(p => p.GetProperty("nonce").GetInt64()).Order());

        foreach (var betId in betIds)
        {
            await WaitForStatusAsync(client, betId, "Settled");
        }

        var rounds = await Task.WhenAll(betIds.Select(id => RoundAsync(client, id)));
        var totalPayout = rounds.Sum(r => r.GetProperty("payout").GetInt64());
        var account = await app.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        Assert.Equal((10_000 - (40 * 10) + totalPayout, 0L), (account.Available, account.Reserved));
        Assert.All(rounds, r => Assert.Equal(r.GetProperty("winningNumber").GetInt32() == 17 ? 360 : 0, r.GetProperty("payout").GetInt64()));
        output.WriteLine($"40 apuestas liquidadas; premios totales {totalPayout}");
    }

    [Fact]
    public async Task When_the_game_never_answers_the_wallet_gives_the_stake_back_and_a_late_result_cannot_pay()
    {
        using var app = StartApp(ttlSeconds: 2, gamesDown: true);
        var (userId, accountId) = await FundedPlayerAsync(app, 1_000);
        using var client = app.ClientFor(userId);
        var wallet = app.Services.GetRequiredService<WalletService>();

        var placed = await PlacedAsync(await PlaceAsync(client, "Red", [], 100, "bet-1"));
        var betId = placed.GetProperty("betId").GetGuid();

        // La Wallet reserva, el juego nunca contesta, y al vencer la reserva las fichas vuelven al jugador.
        await WaitUntilAsync(async () => (await wallet.GetAsync(accountId)).Reserved == 100, "reserva abierta", 30);
        await WaitUntilAsync(async () => (await wallet.GetAsync(accountId)).Reserved == 0, "reserva liberada por vencimiento", 60);
        Assert.Equal(1_000, (await wallet.GetAsync(accountId)).Available);

        // Si el resultado llega despues del vencimiento, la Wallet lo rechaza: no se paga un premio sobre una reserva ya devuelta.
        var late = await TrackPublishAsync(app, new RoundResolved(betId, accountId, 200));

        var rejected = late.Sent.MessagesOf<StakeSettlementRejected>().Single(m => m.BetId == betId);
        Assert.Equal("ReservationNotOpen", rejected.Reason);
        Assert.Equal(1_000, (await wallet.GetAsync(accountId)).Available);
    }

    [Fact]
    public async Task Expiry_and_late_settlement_void_the_round_and_free_the_seed_rotation()
    {
        using var app = StartApp(gamesDown: true);
        var (userId, accountId) = await FundedPlayerAsync(app, 1_000);
        using var client = app.ClientFor(userId);
        var roulette = app.Services.GetRequiredService<RouletteService>();

        // Ronda 1: vence la reserva antes del sorteo.
        var expired = await PlacedAsync(await PlaceAsync(client, "Red", [], 100, "bet-expired"));
        var expiredId = expired.GetProperty("betId").GetGuid();
        await roulette.OnStakeReleasedAsync(new StakeReleased(expiredId, accountId, 100, Guid.NewGuid()));

        // Ronda 2: el juego sortea, pero la liquidacion llega tarde y la Wallet la rechaza.
        var late = await PlacedAsync(await PlaceAsync(client, "Black", [], 100, "bet-late"));
        var lateId = late.GetProperty("betId").GetGuid();
        await roulette.OnStakeReservedAsync(new StakeReserved(lateId, accountId, 100, Guid.NewGuid()));
        await roulette.OnSettlementRejectedAsync(new StakeSettlementRejected(lateId, accountId, "ReservationNotOpen"));

        var expiredRound = await RoundAsync(client, expiredId);
        Assert.Equal("Voided", expiredRound.GetProperty("status").GetString());
        Assert.Equal("ReservationExpired", expiredRound.GetProperty("failureReason").GetString());
        Assert.Equal(JsonValueKind.Null, expiredRound.GetProperty("winningNumber").ValueKind);

        var lateRound = await RoundAsync(client, lateId);
        Assert.Equal("Voided", lateRound.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, lateRound.GetProperty("winningNumber").ValueKind); // el sorteo queda para auditoria

        // Ninguna apuesta pendiente: se puede rotar.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/fairness/me/rotate", content: null)).StatusCode);
    }

    [Fact]
    public async Task A_broker_outage_in_the_middle_of_betting_loses_no_bet_and_blocks_rotation_until_they_close()
    {
        using var app = StartApp();
        var (userId, accountId) = await FundedPlayerAsync(app, 1_000);
        using var client = app.ClientFor(userId);
        List<Guid> betIds = [];

        await rabbit.StopBrokerAsync();
        try
        {
            // Con el broker caido las apuestas se aceptan igual: quedan en el outbox.
            for (var i = 0; i < 5; i++)
            {
                var placed = await PlacedAsync(await PlaceAsync(client, "Odd", [], 10, $"bet-{i}"));
                betIds.Add(placed.GetProperty("betId").GetGuid());
            }

            var blocked = await client.PostAsync("/fairness/me/rotate", content: null);
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            Assert.Equal("PendingBets", (await blocked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        }
        finally
        {
            await rabbit.StartBrokerAsync();
        }

        foreach (var betId in betIds)
        {
            await WaitForStatusAsync(client, betId, "Settled", seconds: 150);
        }

        var rounds = await Task.WhenAll(betIds.Select(id => RoundAsync(client, id)));
        var totalPayout = rounds.Sum(r => r.GetProperty("payout").GetInt64());
        var account = await app.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        Assert.Equal((1_000 - 50 + totalPayout, 0L), (account.Available, account.Reserved));
        Assert.Equal(Enumerable.Range(0, 5).Select(n => (long)n), rounds.Select(r => r.GetProperty("nonce").GetInt64()).Order());

        await WaitUntilAsync(
            async () => (await client.PostAsync("/fairness/me/rotate", content: null)).StatusCode == HttpStatusCode.OK,
            "rotacion permitida cuando cerraron las apuestas");
    }
}
