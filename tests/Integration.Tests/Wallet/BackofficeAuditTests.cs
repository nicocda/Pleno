using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casino.BuildingBlocks;
using Casino.Modules.Wallet.Application;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Casino.Integration.Tests.Wallet;

/// <summary>Toda carga manual de fichas deja una anotacion (quien, a quien, cuanto, cuando) y solo el backoffice puede leerlas.</summary>
[Collection(WalletDbDefinition.Name)]
public sealed class BackofficeAuditTests : IDisposable
{
    private readonly CasinoCluster _app;

    public BackofficeAuditTests(PostgresFixture db)
    {
        _app = TestAuth.StartApp(db);
    }

    public void Dispose() => _app.Dispose();

    private async Task<Guid> PlayerAsync()
    {
        var userId = Guid.NewGuid();
        await _app.Services.GetRequiredService<WalletService>().OpenAccountAsync(userId);
        return userId;
    }

    private static Task<HttpResponseMessage> CreditAsync(HttpClient client, Guid userId, long amount, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/backoffice/wallet/users/{userId}/credit") { Content = JsonContent.Create(new { amount }) };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return client.SendAsync(request);
    }

    private static async Task<List<JsonElement>> AuditOfAsync(HttpClient client, Guid targetUserId)
    {
        var all = await client.GetFromJsonAsync<JsonElement>("/backoffice/wallet/audit?limit=200");
        return [.. all.EnumerateArray().Where(e => e.GetProperty("targetUserId").GetGuid() == targetUserId)];
    }

    [Fact]
    public async Task A_credit_is_recorded_with_who_to_whom_and_how_much()
    {
        var player = await PlayerAsync();
        var admin = Guid.NewGuid();
        using var backoffice = _app.ClientFor(admin, Roles.Backoffice);

        var credit = await (await CreditAsync(backoffice, player, 250, "audit-1")).Content.ReadFromJsonAsync<JsonElement>();

        var entry = Assert.Single(await AuditOfAsync(backoffice, player));
        Assert.Equal("ChipsCredited", entry.GetProperty("action").GetString());
        Assert.Equal(admin, entry.GetProperty("actorUserId").GetGuid());
        Assert.Equal(250, entry.GetProperty("amount").GetInt64());
        Assert.Equal(credit.GetProperty("transactionId").GetGuid(), entry.GetProperty("transactionId").GetGuid());
        Assert.True(DateTimeOffset.UtcNow - entry.GetProperty("occurredAt").GetDateTimeOffset() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Repeating_the_same_credit_does_not_duplicate_the_record()
    {
        var player = await PlayerAsync();
        using var backoffice = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);

        await CreditAsync(backoffice, player, 100, "audit-dup");
        await CreditAsync(backoffice, player, 100, "audit-dup");

        Assert.Single(await AuditOfAsync(backoffice, player));
    }

    [Fact]
    public async Task A_missing_record_is_completed_when_the_credit_is_retried_with_the_same_key()
    {
        var player = await PlayerAsync();
        var admin = Guid.NewGuid();
        using var backoffice = _app.ClientFor(admin, Roles.Backoffice);
        // La carga se hizo, pero la anotacion no llego a guardarse (por ejemplo, cayo el proceso entre ambos pasos).
        await _app.Services.GetRequiredService<WalletService>().CreditAsync(PlayerIds.WalletAccountFor(player), "audit-heal", 70);
        Assert.Empty(await AuditOfAsync(backoffice, player));

        var retry = await CreditAsync(backoffice, player, 70, "audit-heal");

        Assert.True((await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isDuplicate").GetBoolean());
        var entry = Assert.Single(await AuditOfAsync(backoffice, player));
        Assert.Equal(70, entry.GetProperty("amount").GetInt64());
    }

    [Fact]
    public async Task A_rejected_credit_leaves_no_record()
    {
        var player = await PlayerAsync();
        using var backoffice = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);

        Assert.Equal(HttpStatusCode.BadRequest, (await CreditAsync(backoffice, player, -5, "audit-neg")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreditAsync(backoffice, player, 10, key: null)).StatusCode);

        Assert.Empty(await AuditOfAsync(backoffice, player));
    }

    [Fact]
    public async Task The_newest_records_come_first()
    {
        var player = await PlayerAsync();
        using var backoffice = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);

        await CreditAsync(backoffice, player, 1, "order-1");
        await Task.Delay(20);
        await CreditAsync(backoffice, player, 2, "order-2");

        var entries = await AuditOfAsync(backoffice, player);
        Assert.Equal([2L, 1L], entries.Select(e => e.GetProperty("amount").GetInt64()));
    }

    [Fact]
    public async Task Only_backoffice_can_read_the_audit_log()
    {
        using var player = _app.ClientFor(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Forbidden, (await player.GetAsync("/backoffice/wallet/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await player.GetAsync("/backoffice/users")).StatusCode);
        using var anonymous = _app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/backoffice/wallet/audit")).StatusCode);
    }

    [Fact]
    public async Task Backoffice_lists_the_registered_players()
    {
        var userId = Guid.NewGuid();
        using var player = _app.ClientFor(userId);
        Assert.Equal(HttpStatusCode.OK, (await player.GetAsync("/me")).StatusCode); // el primer ingreso lo da de alta
        using var backoffice = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);

        var users = await backoffice.GetFromJsonAsync<JsonElement>("/backoffice/users");

        Assert.Contains(users.EnumerateArray(), u => u.GetProperty("userId").GetGuid() == userId);
    }
}
