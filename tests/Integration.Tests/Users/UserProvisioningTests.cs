using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casino.BuildingBlocks;
using Casino.Integration.Tests.Wallet;
using Casino.Modules.Wallet.Application;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Casino.Integration.Tests.Users;

/// <summary>El alta automatica de un jugador nuevo: perfil minimo, cuenta de la Wallet y fichas de bienvenida, una sola vez.</summary>
[Collection(WalletDbDefinition.Name)]
public sealed class UserProvisioningTests(PostgresFixture db, RabbitMqFixture rabbit) : IDisposable
{
    private const long Welcome = 1_000;
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
        var app = TestAuth.StartApp(db, rabbit, welcomeChips: (int)Welcome);
        _apps.Add(app);
        return app;
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

            await Task.Delay(500);
        }

        throw new TimeoutException($"No se cumplio a tiempo: {what}");
    }

    private async Task<long> ProfileRowsAsync(Guid userId)
    {
        await using var conn = new NpgsqlConnection(db.UsersDbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM casino.mt_doc_userprofile WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", userId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task The_first_request_of_a_new_player_opens_their_account_with_the_welcome_chips()
    {
        var app = StartApp();
        var userId = Guid.NewGuid();
        using var client = app.ClientFor(userId);

        var me = await client.GetAsync("/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        await WaitUntilAsync(
            async () => (await client.GetFromJsonAsync<JsonElement>("/wallet/me")).GetProperty("available").GetInt64() == Welcome,
            "cuenta abierta con las fichas de bienvenida");
    }

    [Fact]
    public async Task Any_first_player_endpoint_provisions_the_user_not_only_me()
    {
        var app = StartApp();
        var userId = Guid.NewGuid();
        using var client = app.ClientFor(userId);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/fairness/me")).StatusCode);

        await WaitUntilAsync(async () => await ProfileRowsAsync(userId) == 1, "perfil creado por un endpoint distinto de /me");
        await WaitUntilAsync(
            async () => (await client.GetFromJsonAsync<JsonElement>("/wallet/me")).GetProperty("available").GetInt64() == Welcome,
            "cuenta abierta");
    }

    [Fact]
    public async Task Concurrent_first_requests_on_two_instances_create_one_profile_and_grant_the_bonus_once()
    {
        var first = StartApp();
        var second = StartApp();
        var userId = Guid.NewGuid();
        using var clientA = first.ClientFor(userId);
        using var clientB = second.ClientFor(userId);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(() =>
            (i % 2 == 0 ? clientA : clientB).GetAsync("/me"))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        await WaitUntilAsync(
            async () => (await clientA.GetFromJsonAsync<JsonElement>("/wallet/me")).GetProperty("available").GetInt64() == Welcome,
            "fichas de bienvenida");

        // Se espera un poco mas: si un duplicado regalara fichas otra vez, tendria que notarse.
        await Task.Delay(TimeSpan.FromSeconds(4));
        var account = await first.Services.GetRequiredService<WalletService>().GetAsync(PlayerIds.WalletAccountFor(userId));
        Assert.Equal(Welcome, account.Available);
        Assert.Equal(2, account.Version); // cuenta abierta + una sola acreditacion
        Assert.Equal(1, await ProfileRowsAsync(userId));
    }

    [Fact]
    public async Task The_profile_stores_no_personal_data()
    {
        var app = StartApp();
        var userId = Guid.NewGuid();
        using var client = app.ClientFor(userId);
        await client.GetAsync("/me"); // el token trae un nombre de usuario; no debe terminar en nuestra base

        await using var conn = new NpgsqlConnection(db.UsersDbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT data::text FROM casino.mt_doc_userprofile WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", userId);
        var json = (string)(await cmd.ExecuteScalarAsync())!;

        using var document = JsonDocument.Parse(json);
        Assert.Equal(["Id", "RegisteredAt"], document.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.DoesNotContain("tester", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_backoffice_only_token_is_not_provisioned_as_a_player()
    {
        var app = StartApp();
        var staffId = Guid.NewGuid();
        using var client = app.ClientFor(staffId, Roles.Backoffice);

        await client.GetAsync($"/backoffice/wallet/users/{Guid.NewGuid()}");
        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.Equal(0, await ProfileRowsAsync(staffId));
    }

    [Fact]
    public async Task Each_player_gets_a_different_account_derived_from_their_user()
    {
        var app = StartApp();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        using var clientA = app.ClientFor(a);
        using var clientB = app.ClientFor(b);

        var meA = await clientA.GetFromJsonAsync<JsonElement>("/me");
        var meB = await clientB.GetFromJsonAsync<JsonElement>("/me");

        Assert.NotEqual(meA.GetProperty("accountId").GetGuid(), meB.GetProperty("accountId").GetGuid());
        Assert.Equal(PlayerIds.WalletAccountFor(a), meA.GetProperty("accountId").GetGuid());
        Assert.NotEqual(a, meA.GetProperty("accountId").GetGuid()); // nunca el mismo Guid que el usuario: chocaria con su stream de seeds
    }
}
