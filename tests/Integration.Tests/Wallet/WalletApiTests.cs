using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casino.BuildingBlocks;
using Casino.Modules.Wallet.Application;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Casino.Integration.Tests.Wallet;

[Collection(WalletDbDefinition.Name)]
public sealed class WalletApiTests : IDisposable
{
    private readonly CasinoCluster _app;

    public WalletApiTests(PostgresFixture db)
    {
        _app = TestAuth.StartApp(db);
    }

    public void Dispose() => _app.Dispose();

    private async Task<Guid> FundedPlayerAsync(long chips)
    {
        var userId = Guid.NewGuid();
        var wallet = _app.Services.GetRequiredService<WalletService>();
        var accountId = await wallet.OpenAccountAsync(userId);
        await wallet.CreditAsync(accountId, "initial-credit", chips);
        return userId;
    }

    private static async Task<HttpResponseMessage> CreditAsync(HttpClient client, Guid userId, long amount, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/backoffice/wallet/users/{userId}/credit")
        {
            Content = JsonContent.Create(new { amount }),
        };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return await client.SendAsync(request);
    }

    [Fact]
    public async Task A_player_reads_only_their_own_account()
    {
        var userId = await FundedPlayerAsync(700);
        var other = await FundedPlayerAsync(5_000);
        using var client = _app.ClientFor(userId);

        var account = await client.GetFromJsonAsync<JsonElement>("/wallet/me");

        Assert.Equal(700, account.GetProperty("available").GetInt64());
        Assert.Equal(PlayerIds.WalletAccountFor(userId), account.GetProperty("accountId").GetGuid());
        Assert.NotEqual(PlayerIds.WalletAccountFor(other), account.GetProperty("accountId").GetGuid());
    }

    [Fact]
    public async Task Audit_endpoint_rebuilds_the_balance_at_a_date()
    {
        var userId = await FundedPlayerAsync(700);
        using var client = _app.ClientFor(userId);
        static string Iso(DateTimeOffset date) => Uri.EscapeDataString(date.UtcDateTime.ToString("o"));

        var now = await client.GetFromJsonAsync<JsonElement>($"/wallet/me/balance?asOf={Iso(DateTimeOffset.UtcNow.AddMinutes(5))}");
        Assert.Equal(700, now.GetProperty("total").GetInt64());

        var beforeOpening = await client.GetAsync($"/wallet/me/balance?asOf={Iso(DateTimeOffset.UtcNow.AddDays(-1))}");
        Assert.Equal(HttpStatusCode.NotFound, beforeOpening.StatusCode);
    }

    [Fact]
    public async Task A_player_without_an_account_gets_not_found()
    {
        using var client = _app.ClientFor(Guid.NewGuid());

        var response = await client.GetAsync("/wallet/me");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Backoffice_credits_a_user_idempotently()
    {
        var userId = await FundedPlayerAsync(100);
        using var backoffice = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);

        var first = await (await CreditAsync(backoffice, userId, 500, "adjust-1")).Content.ReadFromJsonAsync<JsonElement>();
        var second = await (await CreditAsync(backoffice, userId, 500, "adjust-1")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(first.GetProperty("isDuplicate").GetBoolean());
        Assert.True(second.GetProperty("isDuplicate").GetBoolean());
        Assert.Equal(first.GetProperty("transactionId").GetGuid(), second.GetProperty("transactionId").GetGuid());

        var account = await backoffice.GetFromJsonAsync<JsonElement>($"/backoffice/wallet/users/{userId}");
        Assert.Equal(600, account.GetProperty("available").GetInt64());
    }

    [Fact]
    public async Task Backoffice_errors_are_mapped_to_meaningful_status_codes()
    {
        var userId = await FundedPlayerAsync(100);
        using var backoffice = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);
        await CreditAsync(backoffice, userId, 10, "k-credit");

        Assert.Equal(HttpStatusCode.BadRequest, (await CreditAsync(backoffice, userId, 10, key: null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreditAsync(backoffice, userId, -5, "k-negative")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CreditAsync(backoffice, Guid.NewGuid(), 10, "k-ghost")).StatusCode);

        var reused = await CreditAsync(backoffice, userId, 999, "k-credit");
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        Assert.Equal("IdempotencyKeyReused", (await reused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_player_cannot_use_backoffice_endpoints_on_any_account_including_their_own()
    {
        var userId = await FundedPlayerAsync(100);
        using var player = _app.ClientFor(userId);

        Assert.Equal(HttpStatusCode.Forbidden, (await CreditAsync(player, userId, 1_000_000, "give-me-chips")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await player.GetAsync($"/backoffice/wallet/users/{userId}")).StatusCode);
        Assert.Equal(100, (await _app.Services.GetRequiredService<WalletService>().GetAsync(PlayerIds.WalletAccountFor(userId))).Available);
    }

    [Theory]
    [InlineData("reserve")]
    [InlineData("settle")]
    [InlineData("release")]
    [InlineData("reverse")]
    [InlineData("credit")]
    public async Task Internal_wallet_operations_are_not_reachable_over_http(string operation)
    {
        var userId = await FundedPlayerAsync(100);
        using var player = _app.ClientFor(userId);

        var response = await player.PostAsJsonAsync($"/wallet/accounts/{PlayerIds.WalletAccountFor(userId)}/{operation}", new { amount = 10 });

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
    }
}
