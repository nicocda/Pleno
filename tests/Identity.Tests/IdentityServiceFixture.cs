using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casino.Hosts.Identity;
using Casino.Modules.Identity.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

namespace Casino.Identity.Tests;

/// <summary>Guarda los correos en memoria para que las pruebas lean los enlaces.</summary>
public sealed class CapturingEmailSender : IEmailSender
{
    public ConcurrentBag<EmailMessage> Sent { get; } = [];

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        Sent.Add(message);
        return Task.CompletedTask;
    }
}

/// <summary>El servicio de identidad real (host completo) sobre un Postgres real en un contenedor.</summary>
public sealed partial class IdentityServiceFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public WebApplicationFactory<IdentityServiceEntryPoint> Factory { get; private set; } = null!;

    public CapturingEmailSender Emails { get; } = new();

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
            builder.UseSetting("Identity:PublicWebUrl", "http://web.test");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(Emails);
            });
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

    /// <summary>El token del ultimo correo con ese asunto enviado a esa direccion (el enlace trae <c>?token=...</c>).</summary>
    public string TokenFromEmail(string to, string subject)
    {
        var message = Emails.Sent.Where(m => m.To == to && m.Subject == subject).Last();
        return Uri.UnescapeDataString(TokenPattern().Match(message.Body).Groups[1].Value);
    }

    /// <summary>Registra la cuenta y confirma el email con el enlace que llego por correo.</summary>
    public async Task RegisterConfirmedAsync(HttpClient client, string alias, string email, string password)
    {
        var created = await RegisterAsync(client, alias, email, password);
        created.EnsureSuccessStatusCode();
        var verified = await client.PostAsJsonAsync("/identity/verify-email", new { token = TokenFromEmail(email, "Confirma tu email") });
        verified.EnsureSuccessStatusCode();
    }

    public static async Task<(HttpResponseMessage Response, JsonElement Body)> PasswordLoginAsync(HttpClient client, string login, string password) =>
        await PostTokenAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "casino-web",
            ["username"] = login,
            ["password"] = password,
            ["scope"] = "offline_access",
        });

    public static async Task<(HttpResponseMessage Response, JsonElement Body)> RefreshAsync(HttpClient client, string refreshToken) =>
        await PostTokenAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = "casino-web",
            ["refresh_token"] = refreshToken,
        });

    public static async Task<(HttpResponseMessage Response, JsonElement Body)> ExternalCodeLoginAsync(HttpClient client, string code) =>
        await PostTokenAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "urn:casino:grant_type:external_code",
            ["client_id"] = "casino-web",
            ["code"] = code,
        });

    public static JsonElement JwtPayload(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        return JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(payload));
    }

    private static async Task<(HttpResponseMessage Response, JsonElement Body)> PostTokenAsync(HttpClient client, Dictionary<string, string> form)
    {
        var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(form));
        return (response, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    [GeneratedRegex(@"token=([^\s]+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 500)]
    private static partial Regex TokenPattern();
}
