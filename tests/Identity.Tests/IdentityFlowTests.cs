using System.Net;
using Npgsql;
using static Casino.Identity.Tests.IdentityServiceFixture;

namespace Casino.Identity.Tests;

public sealed class IdentityFlowTests(IdentityServiceFixture fixture) : IClassFixture<IdentityServiceFixture>
{
    private const string Password = "una-clave-larga-123";

    private HttpClient Client() => fixture.Factory.CreateClient();

    [Fact]
    public async Task Registrar_y_entrar_con_el_alias_da_un_jwt_sin_email()
    {
        var client = Client();
        await fixture.RegisterConfirmedAsync(client, "ana_flow", "ana.flow@example.com", Password);

        var (response, body) = await PasswordLoginAsync(client, "ana_flow", Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var jwt = body.GetProperty("access_token").GetString()!;
        var claims = JwtPayload(jwt);
        Assert.True(Guid.TryParse(claims.GetProperty("sub").GetString(), out _));
        Assert.Equal("ana_flow", claims.GetProperty("preferred_username").GetString());
        Assert.Equal("player", claims.GetProperty("role").GetString());
        Assert.Contains("casino-api", claims.GetProperty("aud").ToString());
        Assert.DoesNotContain("ana.flow", claims.ToString(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("refresh_token").GetString()));
    }

    [Fact]
    public async Task Se_puede_entrar_con_el_email_en_vez_del_alias()
    {
        var client = Client();
        await fixture.RegisterConfirmedAsync(client, "beto_flow", "beto.flow@example.com", Password);

        var (response, body) = await PasswordLoginAsync(client, "  beto.flow@example.com ", Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("beto_flow", JwtPayload(body.GetProperty("access_token").GetString()!).GetProperty("preferred_username").GetString());
    }

    [Fact]
    public async Task El_email_se_guarda_cifrado_y_nunca_en_claro()
    {
        var client = Client();
        await RegisterAsync(client, "cris_flow", "cris.flow@example.com", Password);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""SELECT to_jsonb(u)::text FROM "AspNetUsers" u WHERE "UserName" = 'cris_flow'""", connection);
        var row = (string)(await command.ExecuteScalarAsync())!;

        Assert.DoesNotContain("cris.flow", row, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EmailCipher", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Un_alias_repetido_da_conflicto()
    {
        var client = Client();
        await RegisterAsync(client, "dani_flow", "dani1.flow@example.com", Password);

        var second = await RegisterAsync(client, "DANI_flow", "dani2.flow@example.com", Password);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Un_email_repetido_no_crea_otra_cuenta_ni_revela_que_existe()
    {
        var client = Client();
        await RegisterAsync(client, "eva_flow", "eva.flow@example.com", Password);

        var second = await RegisterAsync(client, "eva_flow2", "EVA.flow@example.com", Password);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var (login, _) = await PasswordLoginAsync(client, "eva_flow2", Password);
        Assert.Equal(HttpStatusCode.BadRequest, login.StatusCode);
    }

    [Theory]
    [InlineData("x", "valido@example.com", "una-clave-larga-123", "invalid_alias")]
    [InlineData("alias_ok", "no-es-un-email", "una-clave-larga-123", "invalid_email")]
    [InlineData("alias_ok2", "ok2@example.com", "corta", "weak_password")]
    public async Task Rechaza_datos_invalidos(string alias, string email, string password, string error)
    {
        var response = await RegisterAsync(Client(), alias, email, password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(error, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clave_incorrecta_no_entra_y_tras_cinco_fallos_se_bloquea_la_cuenta()
    {
        var client = Client();
        await fixture.RegisterConfirmedAsync(client, "fede_flow", "fede.flow@example.com", Password);

        for (var i = 0; i < 5; i++)
        {
            var (bad, _) = await PasswordLoginAsync(client, "fede_flow", "clave-equivocada-1");
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }

        var (blocked, _) = await PasswordLoginAsync(client, "fede_flow", Password);
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
    }

    [Fact]
    public async Task El_refresh_token_da_un_access_token_nuevo()
    {
        var client = Client();
        await fixture.RegisterConfirmedAsync(client, "gabi_flow", "gabi.flow@example.com", Password);
        var (_, login) = await PasswordLoginAsync(client, "gabi_flow", Password);

        var (response, refreshed) = await RefreshAsync(client, login.GetProperty("refresh_token").GetString()!);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("gabi_flow", JwtPayload(refreshed.GetProperty("access_token").GetString()!).GetProperty("preferred_username").GetString());
    }

    [Fact]
    public async Task Un_refresh_token_inventado_no_sirve()
    {
        var (response, _) = await RefreshAsync(Client(), "token-inventado");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
