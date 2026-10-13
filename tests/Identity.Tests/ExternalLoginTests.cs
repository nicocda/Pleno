using System.Net;
using System.Net.Http.Json;
using Casino.Modules.Identity.Application;
using Casino.Modules.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using static Casino.Identity.Tests.IdentityServiceFixture;

namespace Casino.Identity.Tests;

public sealed class ExternalLoginTests(IdentityServiceFixture fixture) : IClassFixture<IdentityServiceFixture>
{
    private const string Password = "una-clave-larga-123";

    private async Task<(PlayerUser User, string Code)> ExternalLoginAsync(string subject, string email)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
        var tokens = scope.ServiceProvider.GetRequiredService<OneTimeTokenService>();
        var user = await accounts.FindOrCreateExternalAsync("Google", subject, email, CancellationToken.None);
        return (user, await tokens.IssueAsync(user.Id, OneTimeTokenPurpose.ExternalLogin, TimeSpan.FromMinutes(2), CancellationToken.None));
    }

    [Fact]
    public async Task Una_cuenta_nueva_de_Google_recibe_alias_generado_rol_player_y_entra_con_el_codigo()
    {
        var client = fixture.Factory.CreateClient();
        var (user, code) = await ExternalLoginAsync("google-sub-1", "nueva.google@example.com");

        var (response, body) = await ExternalCodeLoginAsync(client, code);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var claims = JwtPayload(body.GetProperty("access_token").GetString()!);
        Assert.Equal(user.Id.ToString(), claims.GetProperty("sub").GetString());
        Assert.StartsWith("jugador_", claims.GetProperty("preferred_username").GetString(), StringComparison.Ordinal);
        Assert.Equal("player", claims.GetProperty("role").GetString());
        Assert.DoesNotContain("google", claims.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task El_codigo_es_de_un_solo_uso_y_uno_inventado_no_sirve()
    {
        var client = fixture.Factory.CreateClient();
        var (_, code) = await ExternalLoginAsync("google-sub-2", "uso.unico@example.com");

        Assert.Equal(HttpStatusCode.OK, (await ExternalCodeLoginAsync(client, code)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ExternalCodeLoginAsync(client, code)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ExternalCodeLoginAsync(client, "inventado")).Response.StatusCode);
    }

    [Fact]
    public async Task Entrar_de_nuevo_con_Google_da_la_misma_cuenta()
    {
        var (first, _) = await ExternalLoginAsync("google-sub-3", "misma.cuenta@example.com");
        var (second, _) = await ExternalLoginAsync("google-sub-3", "misma.cuenta@example.com");

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task Google_con_el_email_de_una_cuenta_verificada_se_vincula_a_esa_cuenta()
    {
        var client = fixture.Factory.CreateClient();
        await fixture.RegisterConfirmedAsync(client, "vinc_ext", "vinc.ext@example.com", Password);

        var (user, _) = await ExternalLoginAsync("google-sub-4", "vinc.ext@example.com");

        Assert.Equal("vinc_ext", user.UserName);
        Assert.Equal(HttpStatusCode.OK, (await PasswordLoginAsync(client, "vinc_ext", Password)).Response.StatusCode); // la clave sigue valiendo
    }

    [Fact]
    public async Task Google_con_el_email_de_una_cuenta_sin_verificar_la_toma_y_le_quita_la_clave()
    {
        var client = fixture.Factory.CreateClient();
        await RegisterAsync(client, "previa_ext", "victima.ext@example.com", Password); // alguien se registra con el email de otro y no lo puede verificar

        var (user, _) = await ExternalLoginAsync("google-sub-5", "victima.ext@example.com");

        Assert.Equal("previa_ext", user.UserName);
        Assert.True(user.EmailConfirmed);
        Assert.Equal(HttpStatusCode.BadRequest, (await PasswordLoginAsync(client, "previa_ext", Password)).Response.StatusCode);
    }

    [Fact]
    public async Task Los_proveedores_dicen_que_Google_no_esta_configurado_y_el_inicio_responde_404()
    {
        var client = fixture.Factory.CreateClient();

        var providers = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/identity/providers");

        Assert.False(providers.GetProperty("google").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/identity/google/start?returnUrl=http://localhost:5173/auth/callback")).StatusCode);
    }
}
