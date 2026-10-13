using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Casino.Hosts.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Casino.Identity.Tests;

/// <summary>El servicio de identidad real (host completo) sobre un Postgres real en un contenedor.</summary>
public sealed class IdentityServiceFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public WebApplicationFactory<IdentityServiceEntryPoint> Factory { get; private set; } = null!;

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        Factory = new WebApplicationFactory<IdentityServiceEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:identitydb", _postgres.GetConnectionString());
            builder.UseSetting("Identity:Email:CurrentKeyVersion", "1");
            builder.UseSetting("Identity:Email:EncryptionKeys:1", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting("Identity:Email:HmacKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting("Identity:RateLimitPerMinute", "10000");
        });
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }

    public static Task<HttpResponseMessage> RegisterAsync(HttpClient client, string alias, string email, string password) =>
        client.PostAsJsonAsync("/identity/register", new { alias, email, password });

    public static async Task<(HttpResponseMessage Response, JsonElement Body)> PasswordLoginAsync(HttpClient client, string login, string password)
    {
        var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "casino-web",
            ["username"] = login,
            ["password"] = password,
            ["scope"] = "offline_access",
        }));
        return (response, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    public static async Task<(HttpResponseMessage Response, JsonElement Body)> RefreshAsync(HttpClient client, string refreshToken)
    {
        var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = "casino-web",
            ["refresh_token"] = refreshToken,
        }));
        return (response, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    public static JsonElement JwtPayload(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        return JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(payload));
    }
}
