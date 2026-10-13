using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casino.Hosts.Identity;
using Casino.Integration.Tests.Wallet;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Casino.Integration.Tests.Users;

/// <summary>
/// El sistema contra el servicio de identidad REAL: los servicios validan firma y emisor con las claves publicas que publica el propio servicio
/// de identidad (descubrimiento OpenID Connect + JWKS) y leen los roles del claim "role". Es lo que la prueba con tokens de laboratorio no puede demostrar.
/// </summary>
[Collection(WalletDbDefinition.Name)]
public sealed class IdentityIntegrationTests(PostgresFixture db) : IAsyncLifetime
{
    private const string Authority = "http://identity.test";

    // Ids fijos de los usuarios de desarrollo (ver DevUsers en el modulo de identidad).
    private static readonly Guid Jugador1 = Guid.Parse("0a1b2c3d-0001-4000-8000-000000000001");

    private WebApplicationFactory<IdentityServiceEntryPoint> _identity = null!;
    private CasinoCluster _app = null!;

    public Task InitializeAsync()
    {
        _identity = new WebApplicationFactory<IdentityServiceEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:identitydb", db.IdentityDbConnectionString);
            builder.UseSetting("Identity:Issuer", Authority);
            builder.UseSetting("Identity:SeedDevUsers", "true");
            builder.UseSetting("Identity:Email:CurrentKeyVersion", "1");
            builder.UseSetting("Identity:Email:EncryptionKeys:1", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting("Identity:Email:HmacKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        });

        _app = TestAuth.StartApp(
            db,
            testIssuer: false,
            customize: builder =>
            {
                builder.UseSetting("Authentication:Authority", Authority);
                // Los servicios descubren las claves del emisor por HTTP; aca el "cable" hacia el servicio de identidad es memoria.
                builder.ConfigureServices(services => services.PostConfigure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    options => options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                        Authority + "/.well-known/openid-configuration",
                        new OpenIdConnectConfigurationRetriever(),
                        new HttpDocumentRetriever(new HttpClient(_identity.Server.CreateHandler())) { RequireHttps = false })));
            });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _app.Dispose();
        await _identity.DisposeAsync();
    }

    private async Task<(HttpResponseMessage Response, JsonElement Body)> TokenAsync(string username, string password, string clientId = "casino-web")
    {
        using var client = _identity.CreateClient();
        var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = clientId,
            ["username"] = username,
            ["password"] = password,
        }));
        return (response, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    private async Task<HttpClient> ClientAsync(string username, string password)
    {
        var (_, body) = await TokenAsync(username, password);
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("access_token").GetString());
        return client;
    }

    private static JsonElement Payload(string jwt)
    {
        var part = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        part = part.PadRight(part.Length + ((4 - (part.Length % 4)) % 4), '=');
        return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(part))).RootElement;
    }

    [Fact]
    public async Task A_real_identity_token_carries_the_claims_the_api_relies_on()
    {
        var (_, body) = await TokenAsync("jugador1", "jugador1-dev");

        var payload = Payload(body.GetProperty("access_token").GetString()!);

        Assert.Equal(Jugador1, Guid.Parse(payload.GetProperty("sub").GetString()!));
        Assert.Equal(Authority + "/", payload.GetProperty("iss").GetString());
        Assert.Contains("casino-api", payload.GetProperty("aud").ToString());
        Assert.Equal("jugador1", payload.GetProperty("preferred_username").GetString());
        Assert.Equal("player", payload.GetProperty("role").GetString());
        Assert.DoesNotContain("casino.test", payload.ToString(), StringComparison.Ordinal); // el email nunca viaja en el token
    }

    [Fact]
    public async Task The_api_accepts_a_real_player_token_and_reads_its_roles()
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
    public async Task A_real_cashier_token_carries_both_roles()
    {
        using var client = await ClientAsync("cajero1", "cajero1-dev");

        var me = await client.GetFromJsonAsync<JsonElement>("/me");

        Assert.Equal(["cashier", "player"], me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).Order().ToArray());
    }

    [Fact]
    public async Task A_real_backoffice_token_reaches_backoffice_but_the_services_still_validate_the_audience_and_roles()
    {
        using var client = await ClientAsync("backoffice1", "backoffice1-dev");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/me")).StatusCode); // backoffice1 tambien es jugador
        var status = (await client.GetAsync($"/backoffice/wallet/users/{Jugador1}")).StatusCode;
        Assert.True(status is HttpStatusCode.OK or HttpStatusCode.NotFound, $"Se esperaba autorizacion; llego {status}.");
    }

    [Fact]
    public async Task A_wrong_password_never_yields_a_token()
    {
        var (response, body) = await TokenAsync("jugador1", "incorrecta");

        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal("invalid_grant", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_unknown_client_cannot_get_tokens()
    {
        var (response, _) = await TokenAsync("jugador1", "jugador1-dev", clientId: "otro-cliente");

        Assert.False(response.IsSuccessStatusCode);
    }
}
