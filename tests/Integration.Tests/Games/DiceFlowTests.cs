using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casino.Integration.Tests.Wallet;
using Casino.Modules.Games.Dice;
using Casino.Modules.Games.Fairness;
using Casino.Modules.Wallet.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Casino.Integration.Tests.Games;

/// <summary>Dados de punta a punta, con Postgres y RabbitMQ reales: API -> (Wallet reserva) -> sorteo -> (Wallet liquida) -> ronda cerrada.</summary>
[Collection(WalletDbDefinition.Name)]
public sealed class DiceFlowTests(PostgresFixture db, RabbitMqFixture rabbit) : IDisposable
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

    private static async Task<HttpResponseMessage> PlaceAsync(HttpClient client, string direction, int chance, long stake, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/games/dice/bets") { Content = JsonContent.Create(new { direction, chance, stake }) };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> PlacedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<JsonElement> RoundAsync(HttpClient client, Guid betId) => client.GetFromJsonAsync<JsonElement>($"/games/dice/rounds/{betId}");

    private static async Task WaitForStatusAsync(HttpClient client, Guid betId, string status, int seconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if ((await RoundAsync(client, betId)).GetProperty("status").GetString() == status)
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

        throw new TimeoutException($"La ronda {betId} no llego a {status} a tiempo.");
    }

    [Fact]
    public async Task A_roll_runs_from_placement_to_settlement_and_the_player_can_verify_it()
    {
        using var app = StartApp();
        var (userId, accountId) = await FundedPlayerAsync(app, 1_000);
        using var client = app.ClientFor(userId);

        var placed = await PlacedAsync(await PlaceAsync(client, "Under", 5_000, 100, "dice-1"));
        var betId = placed.GetProperty("betId").GetGuid();
        var commitment = placed.GetProperty("commitment").GetString()!;

        await WaitForStatusAsync(client, betId, "Settled");

        var round = await RoundAsync(client, betId);
        var roll = round.GetProperty("roll").GetInt32();
        var payout = round.GetProperty("payout").GetInt64();
        Assert.InRange(roll, 0, DiceBet.Rolls - 1);
        Assert.Equal(DiceBet.Create(DiceDirection.Under, 5_000, 100).PayoutFor(roll), payout);

        var account = await app.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        Assert.Equal((1_000 - 100 + payout, 0L), (account.Available, account.Reserved));

        // El jugador rota para que se revele la seed y verifica el sorteo con datos publicos.
        var rotated = await (await client.PostAsync("/fairness/me/rotate", content: null)).Content.ReadFromJsonAsync<JsonElement>();
        var retired = rotated.GetProperty("retired")[0];
        var serverSeed = retired.GetProperty("serverSeed").GetString()!;
        Assert.True(ProvablyFair.MatchesCommitment(serverSeed, commitment));
        Assert.Equal(roll, new FairRng(serverSeed, retired.GetProperty("clientSeed").GetString()!, round.GetProperty("nonce").GetInt64()).NextInt(DiceBet.Rolls));
    }

    [Fact]
    public async Task Retrying_with_the_same_key_does_not_charge_twice_and_another_bet_with_that_key_is_refused()
    {
        using var app = StartApp();
        var (userId, accountId) = await FundedPlayerAsync(app, 1_000);
        using var client = app.ClientFor(userId);

        var first = await PlacedAsync(await PlaceAsync(client, "Over", 2_000, 50, "same-key"));
        var again = await PlacedAsync(await PlaceAsync(client, "Over", 2_000, 50, "same-key"));
        Assert.Equal(first.GetProperty("betId").GetGuid(), again.GetProperty("betId").GetGuid());

        var other = await PlaceAsync(client, "Over", 2_000, 60, "same-key");
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);

        var betId = first.GetProperty("betId").GetGuid();
        await WaitForStatusAsync(client, betId, "Settled");
        var payout = (await RoundAsync(client, betId)).GetProperty("payout").GetInt64();
        var account = await app.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        Assert.Equal(1_000 - 50 + payout, account.Available);
    }

    [Fact]
    public async Task Insufficient_funds_rejects_the_round_without_moving_chips()
    {
        using var app = StartApp();
        var (userId, accountId) = await FundedPlayerAsync(app, 50);
        using var client = app.ClientFor(userId);

        var betId = (await PlacedAsync(await PlaceAsync(client, "Under", 5_000, 500, "poor-1"))).GetProperty("betId").GetGuid();

        await WaitForStatusAsync(client, betId, "Rejected");

        var round = await RoundAsync(client, betId);
        Assert.Equal("InsufficientFunds", round.GetProperty("failureReason").GetString());
        Assert.Equal(JsonValueKind.Null, round.GetProperty("roll").ValueKind);
        Assert.Equal(50, (await app.Services.GetRequiredService<WalletService>().GetAsync(accountId)).Available);
    }

    [Theory]
    [InlineData("Under", 100, 10)]
    [InlineData("Over", 9_600, 10)]
    [InlineData("Sideways", 5_000, 10)]
    [InlineData("Under", 5_000, 0)]
    public async Task Invalid_bets_are_refused_before_anything_is_reserved(string direction, int chance, long stake)
    {
        using var app = StartApp();
        var (userId, _) = await FundedPlayerAsync(app, 1_000);
        using var client = app.ClientFor(userId);

        Assert.Equal(HttpStatusCode.BadRequest, (await PlaceAsync(client, direction, chance, stake, "bad-1")).StatusCode);
    }

    [Fact]
    public async Task Another_players_round_looks_like_it_does_not_exist()
    {
        using var app = StartApp();
        var (userId, _) = await FundedPlayerAsync(app, 1_000);
        using var owner = app.ClientFor(userId);
        var betId = (await PlacedAsync(await PlaceAsync(owner, "Under", 5_000, 10, "mine-1"))).GetProperty("betId").GetGuid();

        using var stranger = app.ClientFor(Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/games/dice/rounds/{betId}")).StatusCode);
    }
}
