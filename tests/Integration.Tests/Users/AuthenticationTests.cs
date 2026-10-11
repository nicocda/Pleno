using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Casino.BuildingBlocks;
using Casino.Integration.Tests.Wallet;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Casino.Integration.Tests.Users;

/// <summary>La puerta de la API: sin token valido no se entra, y con token solo se hace lo que el rol permite.</summary>
[Collection(WalletDbDefinition.Name)]
public sealed class AuthenticationTests : IDisposable
{
    private readonly CasinoCluster _app;

    public AuthenticationTests(PostgresFixture db)
    {
        _app = TestAuth.StartApp(db);
    }

    public void Dispose() => _app.Dispose();

    private HttpClient ClientWith(string token)
    {
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Theory]
    [InlineData("GET", "/me")]
    [InlineData("GET", "/wallet/me")]
    [InlineData("GET", "/wallet/me/balance")]
    [InlineData("GET", "/fairness/me")]
    [InlineData("POST", "/fairness/me/rotate")]
    [InlineData("POST", "/games/roulette/bets")]
    [InlineData("GET", "/games/roulette/rounds")]
    [InlineData("GET", "/backoffice/wallet/users/0a1b2c3d-0001-4000-8000-000000000001")]
    public async Task Every_business_endpoint_requires_a_token(string method, string path)
    {
        using var client = _app.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_landing_endpoint_stays_public()
    {
        using var client = _app.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task A_valid_token_with_the_player_role_gets_in()
    {
        using var client = _app.ClientFor(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task A_token_signed_with_another_key_is_rejected()
    {
        using var client = ClientWith(TestAuth.Token(Guid.NewGuid(), signingKey: TestAuth.NewForeignKey()));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task A_token_meant_for_another_audience_is_rejected()
    {
        using var client = ClientWith(TestAuth.Token(Guid.NewGuid(), audience: "otra-api"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task An_expired_token_is_rejected()
    {
        // Mas de los 5 minutos de tolerancia de reloj que concede JwtBearer.
        using var client = ClientWith(TestAuth.Token(Guid.NewGuid(), lifetime: TimeSpan.FromMinutes(-30)));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task A_garbage_token_is_rejected()
    {
        using var client = ClientWith("esto.no.es-un-jwt");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task A_token_whose_subject_is_not_a_valid_user_id_is_rejected()
    {
        using var client = ClientWith(TestAuth.Token(Guid.NewGuid(), subject: "no-soy-un-uuid"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task A_token_without_roles_is_authenticated_but_forbidden()
    {
        using var client = ClientWith(TestAuth.Token(Guid.NewGuid(), roles: []));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/wallet/me")).StatusCode);
    }

    [Fact]
    public async Task A_malformed_roles_claim_grants_nothing()
    {
        using var client = ClientWith(TestAuth.Token(Guid.NewGuid(), realmAccess: "esto no es json"));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task Roles_are_separate_a_backoffice_user_is_not_a_player()
    {
        using var backoffice = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);

        Assert.Equal(HttpStatusCode.Forbidden, (await backoffice.GetAsync("/wallet/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await backoffice.GetAsync("/games/roulette/rounds")).StatusCode);
    }

    [Fact]
    public async Task Me_reports_the_identity_from_the_token()
    {
        var userId = Guid.NewGuid();
        using var client = ClientWith(TestAuth.Token(userId, username: "alicia", roles: [Roles.Player, Roles.Backoffice]));

        var me = await client.GetFromJsonAsync<JsonElement>("/me");

        Assert.Equal(userId, me.GetProperty("userId").GetGuid());
        Assert.Equal("alicia", me.GetProperty("displayName").GetString());
        Assert.Equal(PlayerIds.WalletAccountFor(userId), me.GetProperty("accountId").GetGuid());
        Assert.Equal([Roles.Backoffice, Roles.Player], me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
    }
}
