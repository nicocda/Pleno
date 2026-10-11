using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casino.Integration.Tests.Wallet;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit.Abstractions;

namespace Casino.Integration.Tests.Users;

/// <summary>
/// La API contra un Keycloak REAL: valida firma y emisor con las claves publicas que publica el propio Keycloak
/// y mapea los roles del realm. Es lo que la prueba con tokens de laboratorio no puede demostrar.
/// </summary>
[Collection(WalletDbDefinition.Name)]
public sealed class KeycloakIntegrationTests(PostgresFixture db, KeycloakFixture keycloak, ITestOutputHelper output) : IAsyncLifetime
{
    // Ids fijos de los usuarios de desarrollo (ver src/Casino.AppHost/realms/casino-realm.json).
    private static readonly Guid Jugador1 = Guid.Parse("0a1b2c3d-0001-4000-8000-000000000001");
    private static readonly Guid Backoffice1 = Guid.Parse("0a1b2c3d-0003-4000-8000-000000000003");

    private CasinoCluster _app = null!;

    public async Task InitializeAsync()
    {
        await keycloak.EnsureStartedAsync();
        _app = TestAuth.StartApp(
            db,
            testIssuer: false,
            customize: builder => builder.UseSetting("Authentication:Authority", keycloak.Authority));
    }

    public Task DisposeAsync()
    {
        _app.Dispose();
        return Task.CompletedTask;
    }

    private async Task<HttpClient> ClientAsync(string username, string password)
    {
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await keycloak.TokenAsync(username, password));
        return client;
    }

    private static JsonElement Payload(string jwt)
    {
        var part = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        part = part.PadRight(part.Length + ((4 - (part.Length % 4)) % 4), '=');
        return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(part))).RootElement;
    }

    [Fact]
    public async Task A_real_keycloak_token_carries_the_claims_the_api_relies_on()
    {
        var token = await keycloak.TokenAsync("jugador1", "jugador1-dev");

        var payload = Payload(token);

        Assert.Equal(Jugador1, Guid.Parse(payload.GetProperty("sub").GetString()!));
        Assert.Equal(keycloak.Authority, payload.GetProperty("iss").GetString());
        Assert.Contains("casino-api", payload.GetProperty("aud").ToString());
        Assert.Equal("jugador1", payload.GetProperty("preferred_username").GetString());
        Assert.Contains("player", payload.GetProperty("realm_access").GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        output.WriteLine($"Emisor: {payload.GetProperty("iss").GetString()}");
    }

    [Fact]
    public async Task The_api_accepts_a_real_player_token_and_maps_its_roles()
    {
        using var client = await ClientAsync("jugador1", "jugador1-dev");

        var response = await client.GetAsync("/me");
        var me = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Jugador1, me.GetProperty("userId").GetGuid());
        Assert.Equal("jugador1", me.GetProperty("displayName").GetString());
        Assert.Contains("player", me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.DoesNotContain("backoffice", me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task A_real_backoffice_token_reaches_backoffice_but_not_player_endpoints()
    {
        using var client = await ClientAsync("backoffice1", "backoffice1-dev");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/wallet/me")).StatusCode);

        var status = (await client.GetAsync($"/backoffice/wallet/users/{Jugador1}")).StatusCode;
        Assert.True(status is HttpStatusCode.OK or HttpStatusCode.NotFound, $"Se esperaba autorizacion; llego {status}.");
        Assert.NotEqual(Guid.Empty, Backoffice1);
    }

    [Fact]
    public async Task A_wrong_password_never_yields_a_token()
    {
        using var http = new HttpClient();

        var response = await http.PostAsync(
            $"{keycloak.Authority}/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "casino-tests",
                ["username"] = "jugador1",
                ["password"] = "incorrecta",
            }));

        // El estandar OAuth responde 400 invalid_grant (no 401) ante credenciales incorrectas: lo que importa es que no hay token.
        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal("invalid_grant", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task The_web_client_cannot_use_the_password_grant()
    {
        // casino-web es un cliente publico de navegador: solo flujo con PKCE. El "grant" con contraseña es solo del cliente de pruebas.
        using var http = new HttpClient();

        var response = await http.PostAsync(
            $"{keycloak.Authority}/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "casino-web",
                ["username"] = "jugador1",
                ["password"] = "jugador1-dev",
            }));

        Assert.False(response.IsSuccessStatusCode);
    }
}
