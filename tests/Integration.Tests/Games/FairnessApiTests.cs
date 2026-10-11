using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casino.Integration.Tests.Wallet;
using Casino.Modules.Games.Fairness;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Casino.Integration.Tests.Games;

[Collection(WalletDbDefinition.Name)]
public sealed class FairnessApiTests : IDisposable
{
    private readonly CasinoCluster _app;

    public FairnessApiTests(PostgresFixture db)
    {
        _app = TestAuth.StartApp(db);
    }

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Public_info_exposes_the_commitment_but_never_the_active_server_seed()
    {
        using var client = _app.ClientFor(Guid.NewGuid());

        var response = await client.GetAsync("/fairness/me");
        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(raw).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Matches("^[0-9a-f]{64}$", body.GetProperty("active").GetProperty("commitment").GetString()!);
        Assert.Equal(0, body.GetProperty("active").GetProperty("nextNonce").GetInt64());
        Assert.DoesNotContain("encrypted", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("serverSeed", body.GetProperty("active").ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Every_player_gets_their_own_independent_seed_pair()
    {
        using var first = _app.ClientFor(Guid.NewGuid());
        using var second = _app.ClientFor(Guid.NewGuid());

        var a = await first.GetFromJsonAsync<JsonElement>("/fairness/me");
        var b = await second.GetFromJsonAsync<JsonElement>("/fairness/me");

        Assert.NotEqual(
            a.GetProperty("active").GetProperty("commitment").GetString(),
            b.GetProperty("active").GetProperty("commitment").GetString());
    }

    [Fact]
    public async Task Rotating_reveals_a_seed_that_matches_the_commitment_published_before()
    {
        using var client = _app.ClientFor(Guid.NewGuid());
        var before = await client.GetFromJsonAsync<JsonElement>("/fairness/me");
        var commitment = before.GetProperty("active").GetProperty("commitment").GetString()!;

        var response = await client.PostAsJsonAsync("/fairness/me/rotate", new { clientSeed = "mi-semilla-nueva" });
        var after = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var retired = after.GetProperty("retired")[0];
        Assert.True(ProvablyFair.MatchesCommitment(retired.GetProperty("serverSeed").GetString()!, commitment));
        Assert.Equal("mi-semilla-nueva", after.GetProperty("active").GetProperty("clientSeed").GetString());
        Assert.NotEqual(commitment, after.GetProperty("active").GetProperty("commitment").GetString());
    }

    [Fact]
    public async Task Rotating_without_a_body_generates_a_random_client_seed()
    {
        using var client = _app.ClientFor(Guid.NewGuid());

        var response = await client.PostAsync("/fairness/me/rotate", content: null);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Matches("^[0-9a-f]{16}$", body.GetProperty("active").GetProperty("clientSeed").GetString()!);
    }

    [Fact]
    public async Task An_invalid_client_seed_is_a_bad_request()
    {
        using var client = _app.ClientFor(Guid.NewGuid());

        var response = await client.PostAsJsonAsync("/fairness/me/rotate", new { clientSeed = new string('x', 65) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("InvalidClientSeed", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task The_api_accepts_no_way_to_choose_a_nonce_or_a_server_seed()
    {
        using var client = _app.ClientFor(Guid.NewGuid());
        var before = await client.GetFromJsonAsync<JsonElement>("/fairness/me");

        // Aunque el cliente mande campos extra, el servidor los ignora: ni nonce ni server seed se pueden imponer.
        var response = await client.PostAsJsonAsync(
            "/fairness/me/rotate",
            new { clientSeed = "ok", nonce = 999, serverSeed = new string('a', 64), nextNonce = 999 });
        var after = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, after.GetProperty("active").GetProperty("nextNonce").GetInt64());
        Assert.NotEqual(new string('a', 64), after.GetProperty("retired")[0].GetProperty("serverSeed").GetString());
        Assert.NotEqual(
            before.GetProperty("active").GetProperty("commitment").GetString(),
            after.GetProperty("active").GetProperty("commitment").GetString());
    }
}
