using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Casino.BuildingBlocks;
using Casino.Modules.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using static Casino.Identity.Tests.IdentityServiceFixture;

namespace Casino.Identity.Tests;

public sealed class AdminRolesTests(IdentityServiceFixture fixture) : IClassFixture<IdentityServiceFixture>
{
    private const string Password = "una-clave-larga-123";
    private static readonly string[] CashierOnly = ["cashier"];
    private static readonly string[] Unknown = ["dios"];
    private static readonly string[] ExpectedRoles = ["cashier", "player"];

    private async Task<HttpClient> AdminClientAsync(string alias)
    {
        var client = fixture.Factory.CreateClient();
        await fixture.RegisterConfirmedAsync(client, alias, $"{alias}@example.com", Password);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<PlayerUser>>();
            await users.AddToRoleAsync((await users.FindByNameAsync(alias))!, Roles.Backoffice);
        }

        var (_, login) = await PasswordLoginAsync(client, alias, Password);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("access_token").GetString());
        return client;
    }

    private static async Task<Guid> IdOfAsync(HttpClient admin, string alias)
    {
        var list = await admin.GetFromJsonAsync<JsonElement>($"/identity/admin/users?q={alias}");
        return list.EnumerateArray().Single(u => u.GetProperty("alias").GetString() == alias).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Sin_token_o_sin_rol_de_backoffice_no_se_puede_administrar()
    {
        var anonymous = fixture.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/identity/admin/users")).StatusCode);

        var player = fixture.Factory.CreateClient();
        await fixture.RegisterConfirmedAsync(player, "plain_adm", "plain_adm@example.com", Password);
        var (_, login) = await PasswordLoginAsync(player, "plain_adm", Password);
        player.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("access_token").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await player.GetAsync("/identity/admin/users")).StatusCode);
    }

    [Fact]
    public async Task El_backoffice_lista_alias_sin_exponer_emails()
    {
        var admin = await AdminClientAsync("admin_list");

        var body = await admin.GetStringAsync("/identity/admin/users?q=admin_list");

        Assert.Contains("admin_list", body, StringComparison.Ordinal);
        Assert.DoesNotContain("@example.com", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dar_un_rol_lo_refleja_en_el_proximo_token_y_cierra_las_sesiones_viejas()
    {
        var admin = await AdminClientAsync("admin_roles");
        var anon = fixture.Factory.CreateClient();
        await fixture.RegisterConfirmedAsync(anon, "cajero_adm", "cajero_adm@example.com", Password);
        var (_, before) = await PasswordLoginAsync(anon, "cajero_adm", Password);
        var id = await IdOfAsync(admin, "cajero_adm");

        var set = await admin.PutAsJsonAsync($"/identity/admin/users/{id}/roles", new { roles = CashierOnly });

        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await RefreshAsync(anon, before.GetProperty("refresh_token").GetString()!)).Response.StatusCode);
        var (_, after) = await PasswordLoginAsync(anon, "cajero_adm", Password);
        var roles = JwtPayload(after.GetProperty("access_token").GetString()!).GetProperty("role").EnumerateArray().Select(r => r.GetString()!).Order().ToArray();
        Assert.Equal(ExpectedRoles, roles);
    }

    [Fact]
    public async Task No_acepta_roles_desconocidos_ni_que_un_admin_se_quite_su_propio_rol()
    {
        var admin = await AdminClientAsync("admin_self");
        var id = await IdOfAsync(admin, "admin_self");

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/identity/admin/users/{id}/roles", new { roles = Unknown })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/identity/admin/users/{id}/roles", new { roles = CashierOnly })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"/identity/admin/users/{Guid.NewGuid()}/roles", new { roles = CashierOnly })).StatusCode);
    }
}
