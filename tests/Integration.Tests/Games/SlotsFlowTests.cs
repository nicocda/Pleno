using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casino.Integration.Tests.Wallet;
using Casino.Modules.Games.Fairness;
using Casino.Modules.Games.Slots;
using Casino.Modules.Wallet.Application;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Casino.Integration.Tests.Games;

/// <summary>
/// La tragamonedas de punta a punta, con Postgres y RabbitMQ reales y un jugador autenticado. Reutiliza sin cambios la Wallet,
/// el nonce y las seeds de la ruleta: API -> (Wallet reserva) -> giro con el nonce del servidor -> (Wallet liquida) -> cerrado.
/// </summary>
[Collection(WalletDbDefinition.Name)]
public sealed class SlotsFlowTests(PostgresFixture db, RabbitMqFixture rabbit) : IDisposable
{
    private readonly List<CasinoCluster> _apps = [];

    public void Dispose()
    {
        foreach (var app in _apps)
        {
            app.Dispose();
        }
    }

    private CasinoCluster StartApp()
    {
        var app = TestAuth.StartApp(db, rabbit);
        _apps.Add(app);
        return app;
    }

    private static async Task<(Guid UserId, Guid AccountId)> FundedPlayerAsync(CasinoCluster app, long chips)
    {
        var userId = Guid.NewGuid();
        var wallet = app.Services.GetRequiredService<WalletService>();
        var accountId = await wallet.OpenAccountAsync(userId);
        await wallet.CreditAsync(accountId, "initial-credit", chips);
        return (userId, accountId);
    }

    private static async Task<HttpResponseMessage> SpinAsync(HttpClient client, long stake, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/games/slots/spins") { Content = JsonContent.Create(new { stake }) };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> AcceptedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<JsonElement> SpinStateAsync(HttpClient client, Guid betId) =>
        client.GetFromJsonAsync<JsonElement>($"/games/slots/spins/{betId}");

    private static async Task WaitForStatusAsync(HttpClient client, Guid betId, string status, string path = "slots/spins", int seconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if ((await client.GetFromJsonAsync<JsonElement>($"/games/{path}/{betId}")).GetProperty("status").GetString() == status)
                {
                    return;
                }
            }
            catch (Exception)
            {
                // Todavia no esta listo.
            }

            await Task.Delay(300);
        }

        throw new TimeoutException($"{path}/{betId} no llego a {status}");
    }

    [Fact]
    public async Task A_spin_runs_from_placement_to_settlement_and_the_player_can_verify_it()
    {
        using var app = StartApp();
        var (userId, accountId) = await FundedPlayerAsync(app, 1_000);
        using var client = app.ClientFor(userId);

        var placed = await AcceptedAsync(await SpinAsync(client, 100, "spin-1"));
        var betId = placed.GetProperty("betId").GetGuid();
        var commitment = placed.GetProperty("commitment").GetString()!;

        await WaitForStatusAsync(client, betId, "Settled");

        var spin = await SpinStateAsync(client, betId);
        var payout = spin.GetProperty("payout").GetInt64();
        var multiplier = spin.GetProperty("multiplier").GetInt64();
        Assert.Equal(3, spin.GetProperty("reels").GetArrayLength());
        Assert.Equal(100 * multiplier, payout);

        var account = await app.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        Assert.Equal((1_000 - 100 + payout, 0L), (account.Available, account.Reserved));

        // El jugador rota para que se revele la seed y recalcula el giro con datos publicos.
        var rotated = await (await client.PostAsync("/fairness/me/rotate", content: null)).Content.ReadFromJsonAsync<JsonElement>();
        var retired = rotated.GetProperty("retired")[0];
        var serverSeed = retired.GetProperty("serverSeed").GetString()!;
        Assert.True(ProvablyFair.MatchesCommitment(serverSeed, commitment));
        var recomputed = SlotsGame.Play(
            SlotsPaytable.Default, 100, serverSeed, retired.GetProperty("clientSeed").GetString()!, spin.GetProperty("nonce").GetInt64());
        Assert.Equal(spin.GetProperty("reels").EnumerateArray().Select(r => r.GetString()), recomputed.Reels);
    }

    [Fact]
    public async Task A_player_who_leaves_right_after_spinning_still_gets_the_spin_resolved_and_paid_by_the_backend()
    {
        using var app = StartApp();
        var (userId, accountId) = await FundedPlayerAsync(app, 1_000);
        var wallet = app.Services.GetRequiredService<WalletService>();

        // El jugador gira y se va (cierra la pestana, vuelve al lobby): el cliente deja de existir sin esperar el resultado.
        Guid betId;
        using (var client = app.ClientFor(userId))
        {
            betId = (await AcceptedAsync(await SpinAsync(client, 100, "left-1"))).GetProperty("betId").GetGuid();
        }

        // Las fichas se descontaron al apostar y el backend cierra solo: el saldo termina en 1.000 - 100 + premio, sin nada reservado.
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline && (await wallet.GetAsync(accountId)).Reserved != 0)
        {
            await Task.Delay(300);
        }

        // Cuando vuelve, la jugada ya esta resuelta y el premio (si hubo) esta en su saldo.
        using var back = app.ClientFor(userId);
        await WaitForStatusAsync(back, betId, "Settled");
        var payout = (await SpinStateAsync(back, betId)).GetProperty("payout").GetInt64();
        var account = await wallet.GetAsync(accountId);
        Assert.Equal((1_000 - 100 + payout, 0L), (account.Available, account.Reserved));
    }

    [Fact]
    public async Task Insufficient_funds_reject_the_spin_and_nothing_is_taken()
    {
        using var app = StartApp();
        var (userId, accountId) = await FundedPlayerAsync(app, 50);
        using var client = app.ClientFor(userId);

        var placed = await AcceptedAsync(await SpinAsync(client, 100, "spin-poor"));

        await WaitForStatusAsync(client, placed.GetProperty("betId").GetGuid(), "Rejected");
        var account = await app.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        Assert.Equal((50L, 0L), (account.Available, account.Reserved));
    }

    [Fact]
    public async Task Repeating_a_request_reserves_once_and_the_same_key_with_another_stake_is_a_conflict()
    {
        using var app = StartApp();
        var (userId, accountId) = await FundedPlayerAsync(app, 1_000);
        using var client = app.ClientFor(userId);

        var first = await AcceptedAsync(await SpinAsync(client, 100, "same-key"));
        var second = await AcceptedAsync(await SpinAsync(client, 100, "same-key"));

        Assert.Equal(first.GetProperty("betId").GetGuid(), second.GetProperty("betId").GetGuid());
        Assert.True(second.GetProperty("alreadyPlaced").GetBoolean());
        var betId = first.GetProperty("betId").GetGuid();
        await WaitForStatusAsync(client, betId, "Settled");
        var payout = (await SpinStateAsync(client, betId)).GetProperty("payout").GetInt64();
        var account = await app.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        Assert.Equal(1_000 - 100 + payout, account.Available); // una sola vez: nunca -200

        Assert.Equal(HttpStatusCode.Conflict, (await SpinAsync(client, 999, "same-key")).StatusCode);
    }

    [Fact]
    public async Task Stakes_outside_the_allowed_range_are_rejected_before_anything_is_reserved()
    {
        using var app = StartApp();
        var (userId, accountId) = await FundedPlayerAsync(app, 100_000);
        using var client = app.ClientFor(userId);

        Assert.Equal(HttpStatusCode.BadRequest, (await SpinAsync(client, 0, "k0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SpinAsync(client, -5, "k1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SpinAsync(client, SlotsPaytable.Default.MaxStake + 1, "k2")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SpinAsync(client, 10, string.Empty)).StatusCode);

        var account = await app.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        Assert.Equal((100_000L, 0L), (account.Available, account.Reserved));
    }

    [Fact]
    public async Task The_paytable_and_the_return_are_public_for_players_but_a_stranger_cannot_see_someone_elses_spin()
    {
        using var app = StartApp();
        var (userId, _) = await FundedPlayerAsync(app, 1_000);
        var (otherId, _) = await FundedPlayerAsync(app, 1_000);
        using var client = app.ClientFor(userId);
        using var other = app.ClientFor(otherId);

        var table = await client.GetFromJsonAsync<JsonElement>("/games/slots/paytable");
        Assert.Equal(3, table.GetProperty("reels").GetInt32());
        Assert.Equal(6, table.GetProperty("symbols").GetArrayLength());
        Assert.InRange(table.GetProperty("returnToPlayerPercent").GetDouble(), 96.3, 96.9);

        var placed = await AcceptedAsync(await SpinAsync(client, 10, "mine"));
        var betId = placed.GetProperty("betId").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/games/slots/spins/{betId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/games/slots/spins/{betId}")).StatusCode);
    }

    [Fact]
    public async Task A_roulette_bet_and_slot_spins_share_the_account_and_each_game_only_handles_its_own()
    {
        using var app = StartApp();
        var (userId, accountId) = await FundedPlayerAsync(app, 10_000);
        using var client = app.ClientFor(userId);

        var rouletteRequest = new HttpRequestMessage(HttpMethod.Post, "/games/roulette/bets")
        {
            Content = JsonContent.Create(new { betType = "Red", selection = new List<int>(), stake = 100L }),
        };
        rouletteRequest.Headers.Add("Idempotency-Key", "mix-roulette");
        var roulette = await AcceptedAsync(await client.SendAsync(rouletteRequest));
        var spins = await Task.WhenAll(Enumerable.Range(0, 5).Select(async i => await AcceptedAsync(await SpinAsync(client, 100, $"mix-{i}"))));

        await WaitForStatusAsync(client, roulette.GetProperty("betId").GetGuid(), "Settled", "roulette/rounds");
        foreach (var spin in spins)
        {
            await WaitForStatusAsync(client, spin.GetProperty("betId").GetGuid(), "Settled");
        }

        // Todos comparten un unico flujo de nonces del servidor: sin repetidos ni huecos.
        var nonces = spins.Select(s => s.GetProperty("nonce").GetInt64()).Append(roulette.GetProperty("nonce").GetInt64()).Order().ToList();
        Assert.Equal(Enumerable.Range(0, 6).Select(i => (long)i), nonces);

        var rouletteRound = await client.GetFromJsonAsync<JsonElement>($"/games/roulette/rounds/{roulette.GetProperty("betId").GetGuid()}");
        var payouts = rouletteRound.GetProperty("payout").GetInt64();
        foreach (var spin in spins)
        {
            payouts += (await SpinStateAsync(client, spin.GetProperty("betId").GetGuid())).GetProperty("payout").GetInt64();
        }

        var account = await app.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        Assert.Equal((10_000 - 600 + payouts, 0L), (account.Available, account.Reserved));
    }
}
