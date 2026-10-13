using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casino.Modules.Identity.Application;
using Casino.Modules.Identity.Infrastructure;

namespace Casino.Identity.Tests;

public sealed class DevMailboxTests(IdentityServiceFixture fixture) : IClassFixture<IdentityServiceFixture>
{
    [Fact]
    public void El_buzon_de_desarrollo_guarda_solo_los_ultimos_50()
    {
        var mailbox = new DevMailbox();

        for (var i = 0; i < 60; i++)
        {
            mailbox.Add(new EmailMessage("a@example.com", $"asunto {i}", "cuerpo"));
        }

        var all = mailbox.Snapshot();
        Assert.Equal(50, all.Count);
        Assert.Equal("asunto 10", all[0].Subject);
        Assert.Equal("asunto 59", all[^1].Subject);
    }

    [Fact]
    public async Task El_endpoint_de_correos_de_desarrollo_existe_solo_con_el_buzon_registrado()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync("/identity/dev/emails");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // en Development sin SMTP el buzon esta registrado
        Assert.Equal(JsonValueKind.Array, (await response.Content.ReadFromJsonAsync<JsonElement>()).ValueKind);
    }
}
