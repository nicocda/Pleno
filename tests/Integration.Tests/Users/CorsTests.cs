using System.Net;
using Casino.Integration.Tests.Wallet;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Casino.Integration.Tests.Users;

/// <summary>El front vive en otro origen: solo el configurado puede llamar a la API desde un navegador.</summary>
[Collection(WalletDbDefinition.Name)]
public sealed class CorsTests(PostgresFixture db) : IDisposable
{
    private const string FrontOrigin = "http://localhost:5173";
    private readonly List<CasinoCluster> _apps = [];

    public void Dispose()
    {
        foreach (var app in _apps)
        {
            app.Dispose();
        }
    }

    private CasinoCluster StartApp(bool withCors)
    {
        var app = TestAuth.StartApp(db, customize: builder =>
        {
            if (withCors)
            {
                builder.UseSetting("Cors:AllowedOrigins:0", FrontOrigin);
            }
        });
        _apps.Add(app);
        return app;
    }

    private static HttpRequestMessage Preflight(string origin, string path = "/wallet/me")
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type,idempotency-key");
        return request;
    }

    private static string? AllowedOrigin(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values) ? values.Single() : null;

    [Fact]
    public async Task The_configured_front_origin_passes_the_preflight_with_the_headers_it_needs()
    {
        using var client = StartApp(withCors: true).CreateClient();

        var response = await client.SendAsync(Preflight(FrontOrigin));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(FrontOrigin, AllowedOrigin(response));
        var allowedHeaders = string.Join(',', response.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Contains("authorization", allowedHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("idempotency-key", allowedHeaders, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Any_other_origin_is_not_allowed()
    {
        using var client = StartApp(withCors: true).CreateClient();

        var response = await client.SendAsync(Preflight("https://sitio-malicioso.example"));

        Assert.Null(AllowedOrigin(response));
    }

    [Fact]
    public async Task The_origin_is_allowed_exactly_not_by_prefix()
    {
        using var client = StartApp(withCors: true).CreateClient();

        Assert.Null(AllowedOrigin(await client.SendAsync(Preflight("http://localhost:51730"))));
        Assert.Null(AllowedOrigin(await client.SendAsync(Preflight("http://localhost:5173.evil.example"))));
        Assert.Null(AllowedOrigin(await client.SendAsync(Preflight("https://localhost:5173"))));
    }

    [Fact]
    public async Task Without_configuration_no_origin_is_allowed()
    {
        using var client = StartApp(withCors: false).CreateClient();

        var response = await client.SendAsync(Preflight(FrontOrigin));

        Assert.Null(AllowedOrigin(response));
    }

    [Fact]
    public async Task A_real_request_from_the_front_carries_the_allow_header()
    {
        var app = StartApp(withCors: true);
        using var client = app.ClientFor(Guid.NewGuid());
        var request = new HttpRequestMessage(HttpMethod.Get, "/me");
        request.Headers.Add("Origin", FrontOrigin);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(FrontOrigin, AllowedOrigin(response));
    }

    [Fact]
    public async Task Cors_does_not_open_the_api_a_request_without_a_token_is_still_unauthorized()
    {
        using var client = StartApp(withCors: true).CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/wallet/me");
        request.Headers.Add("Origin", FrontOrigin);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
