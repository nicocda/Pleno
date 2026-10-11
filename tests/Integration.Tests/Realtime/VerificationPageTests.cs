using System.Net;
using Casino.Integration.Tests.Wallet;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Casino.Integration.Tests.Realtime;

/// <summary>La pagina de verificacion es publica: no lleva secretos y funciona sin iniciar sesion.</summary>
[Collection(WalletDbDefinition.Name)]
public sealed class VerificationPageTests : IDisposable
{
    private readonly CasinoCluster _app;

    public VerificationPageTests(PostgresFixture db)
    {
        _app = TestAuth.StartApp(db);
    }

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task The_page_is_served_without_a_token()
    {
        using var client = _app.CreateClient();

        var response = await client.GetAsync("/verify/index.html");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Verificar una jugada", html, StringComparison.Ordinal);
        Assert.Contains("./verify.js", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_verification_script_is_served_as_javascript()
    {
        using var client = _app.CreateClient();

        var response = await client.GetAsync("/verify/verify.js");
        var script = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("javascript", response.Content.Headers.ContentType?.MediaType, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("verifyRound", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_page_never_embeds_server_data_or_secrets()
    {
        using var client = _app.CreateClient();

        var html = await client.GetStringAsync("/verify/index.html");
        var script = await client.GetStringAsync("/verify/verify.js");

        // Todo se calcula en el navegador: ni la pagina ni el script llaman a la API ni llevan claves o tokens.
        foreach (var content in new[] { html, script })
        {
            Assert.DoesNotContain("fetch(", content, StringComparison.Ordinal);
            Assert.DoesNotContain("XMLHttpRequest", content, StringComparison.Ordinal);
            Assert.DoesNotContain("Bearer", content, StringComparison.Ordinal);
            Assert.DoesNotContain("MasterKey", content, StringComparison.OrdinalIgnoreCase);
        }
    }
}
