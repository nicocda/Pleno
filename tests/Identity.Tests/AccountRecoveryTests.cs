using System.Net;
using System.Net.Http.Json;
using static Casino.Identity.Tests.IdentityServiceFixture;

namespace Casino.Identity.Tests;

public sealed class AccountRecoveryTests(IdentityServiceFixture fixture) : IClassFixture<IdentityServiceFixture>
{
    private const string Password = "una-clave-larga-123";

    private HttpClient Client() => fixture.Factory.CreateClient();

    [Fact]
    public async Task Sin_verificar_el_email_no_se_puede_entrar_y_con_el_enlace_si()
    {
        var client = Client();
        await RegisterAsync(client, "ines_rec", "ines.rec@example.com", Password);

        var (blocked, body) = await PasswordLoginAsync(client, "ines_rec", Password);
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Equal("email_not_verified", body.GetProperty("error_description").GetString());

        var verified = await client.PostAsJsonAsync("/identity/verify-email", new { token = fixture.TokenFromEmail("ines.rec@example.com", "Confirma tu email") });
        Assert.Equal(HttpStatusCode.NoContent, verified.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PasswordLoginAsync(client, "ines_rec", Password)).Response.StatusCode);
    }

    [Fact]
    public async Task Un_enlace_de_verificacion_solo_sirve_una_vez_y_uno_inventado_no_sirve()
    {
        var client = Client();
        await RegisterAsync(client, "juan_rec", "juan.rec@example.com", Password);
        var token = fixture.TokenFromEmail("juan.rec@example.com", "Confirma tu email");

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/identity/verify-email", new { token })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/identity/verify-email", new { token })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/identity/verify-email", new { token = "inventado" })).StatusCode);
    }

    [Fact]
    public async Task Se_puede_reenviar_la_verificacion_y_siempre_responde_igual()
    {
        var client = Client();
        await RegisterAsync(client, "kari_rec", "kari.rec@example.com", Password);
        var before = fixture.Emails.Sent.Count(m => m.To == "kari.rec@example.com");

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/identity/resend-verification", new { email = "kari.rec@example.com" })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/identity/resend-verification", new { email = "nadie@example.com" })).StatusCode);

        Assert.Equal(before + 1, fixture.Emails.Sent.Count(m => m.To == "kari.rec@example.com"));
        Assert.DoesNotContain(fixture.Emails.Sent, m => m.To == "nadie@example.com");
    }

    [Fact]
    public async Task Registrar_un_email_que_ya_tiene_cuenta_avisa_al_dueño_sin_crear_otra()
    {
        var client = Client();
        await fixture.RegisterConfirmedAsync(client, "lu_rec", "lu.rec@example.com", Password);

        var second = await RegisterAsync(client, "lu_rec_otro", "lu.rec@example.com", Password);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Contains(fixture.Emails.Sent, m => m.To == "lu.rec@example.com" && m.Subject == "Ya tienes una cuenta");
    }

    [Fact]
    public async Task Recuperar_la_clave_cambia_la_clave_cierra_sesiones_y_el_enlace_es_de_un_solo_uso()
    {
        var client = Client();
        await fixture.RegisterConfirmedAsync(client, "mati_rec", "mati.rec@example.com", Password);
        var (_, login) = await PasswordLoginAsync(client, "mati_rec", Password);
        var oldRefresh = login.GetProperty("refresh_token").GetString()!;

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/identity/forgot-password", new { email = "mati.rec@example.com" })).StatusCode);
        var token = fixture.TokenFromEmail("mati.rec@example.com", "Recupera tu clave");

        var reset = await client.PostAsJsonAsync("/identity/reset-password", new { token, newPassword = "otra-clave-nueva-456" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PasswordLoginAsync(client, "mati_rec", Password)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PasswordLoginAsync(client, "mati_rec", "otra-clave-nueva-456")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await RefreshAsync(client, oldRefresh)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/identity/reset-password", new { token, newPassword = "y-otra-mas-789012" })).StatusCode);
    }

    [Fact]
    public async Task Una_clave_debil_no_gasta_el_enlace()
    {
        var client = Client();
        await fixture.RegisterConfirmedAsync(client, "nora_rec", "nora.rec@example.com", Password);
        await client.PostAsJsonAsync("/identity/forgot-password", new { email = "nora.rec@example.com" });
        var token = fixture.TokenFromEmail("nora.rec@example.com", "Recupera tu clave");

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/identity/reset-password", new { token, newPassword = "corta" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/identity/reset-password", new { token, newPassword = "una-clave-valida-1" })).StatusCode);
    }

    [Fact]
    public async Task Olvide_mi_clave_con_un_email_desconocido_responde_igual_y_no_envia_nada()
    {
        var response = await Client().PostAsJsonAsync("/identity/forgot-password", new { email = "fantasma@example.com" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.DoesNotContain(fixture.Emails.Sent, m => m.To == "fantasma@example.com");
    }
}
