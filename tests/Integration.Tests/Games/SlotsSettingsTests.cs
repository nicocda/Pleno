using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casino.BuildingBlocks;
using Casino.Integration.Tests.Wallet;
using Casino.Modules.Games.Application;
using Casino.Modules.Games.Slots;
using Casino.Modules.Wallet.Application;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Casino.Integration.Tests.Games;

/// <summary>
/// El administrador edita la tabla de pagos de la tragamonedas desde el panel: se prueba antes de guardar, se publica como una version nueva
/// (con quien y cuando) y los giros posteriores usan la nueva. Usa su PROPIA base de datos para no alterar la tabla que ven los otros tests.
/// </summary>
public sealed class SlotsSettingsTests(PostgresFixture db, RabbitMqFixture rabbit) : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>, IDisposable
{
    private readonly List<CasinoCluster> _apps = [];

    public void Dispose()
    {
        foreach (var app in _apps)
        {
            app.Dispose();
        }
    }

    private CasinoCluster StartApp()
    {
        var app = TestAuth.StartApp(db, rabbit);
        _apps.Add(app);
        return app;
    }

    private static object[] Symbols(params (string Name, int Weight, long Triple)[] symbols) =>
        [.. symbols.Select(s => new { name = s.Name, weight = s.Weight, triplePayout = s.Triple })];

    private static object Body(object[] symbols, long maxStake = 10_000, int baseVersion = 0, object[]? leading = null) =>
        new { symbols, leadingPays = leading ?? [], maxStake, baseVersion };

    private static readonly string[] NewSymbols = ["A", "B"];

    private static readonly object[] Fair = Symbols(("A", 3, 1), ("B", 1, 3)); // retorno 48,4 % (46,9 % sin cascadas)

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, object body) => client.PostAsJsonAsync("/backoffice/games/slots/settings", body);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task The_admin_sees_the_current_table_which_starts_as_the_one_in_the_configuration()
    {
        using var app = StartApp();
        using var admin = app.ClientFor(Guid.NewGuid(), Roles.Backoffice);

        var settings = await admin.GetFromJsonAsync<JsonElement>("/backoffice/games/slots/settings");

        // La base de datos se comparte entre las pruebas de esta clase: la version vigente depende de cuales corrieron antes,
        // pero la de la configuracion (version 0) siempre esta disponible para volver a ella.
        Assert.True(settings.GetProperty("version").GetInt32() >= 0);
        Assert.Equal(6, settings.GetProperty("baseline").GetProperty("symbols").GetArrayLength());
        Assert.InRange(settings.GetProperty("baseline").GetProperty("returnToPlayerPercent").GetDouble(), 96.3, 96.9);
        Assert.Equal(10_000, settings.GetProperty("baseline").GetProperty("maxStake").GetInt64());
    }

    [Fact]
    public async Task Only_backoffice_can_see_or_change_the_settings()
    {
        using var app = StartApp();
        using var player = app.ClientFor(Guid.NewGuid());
        using var anonymous = app.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await player.GetAsync("/backoffice/games/slots/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(player, Body(Fair))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await player.PostAsJsonAsync("/backoffice/games/slots/settings/preview", Body(Fair))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await player.GetAsync("/backoffice/games/slots/settings/history")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/backoffice/games/slots/settings")).StatusCode);
    }

    [Fact]
    public async Task Previewing_shows_the_return_and_the_hit_rate_and_explains_an_invalid_table_without_saving_anything()
    {
        using var app = StartApp();
        using var admin = app.ClientFor(Guid.NewGuid(), Roles.Backoffice);

        var before = (await admin.GetFromJsonAsync<JsonElement>("/backoffice/games/slots/settings")).GetProperty("version").GetInt32();
        var good = await JsonAsync(await admin.PostAsJsonAsync("/backoffice/games/slots/settings/preview", Body(Fair)));
        var greedy = await JsonAsync(await admin.PostAsJsonAsync("/backoffice/games/slots/settings/preview", Body(Symbols(("A", 1, 50), ("B", 1, 50)))));

        Assert.True(good.GetProperty("valid").GetBoolean());
        Assert.InRange(good.GetProperty("returnToPlayerPercent").GetDouble(), 48.3, 48.5); // 46,875 sin cascadas (27 * 1 + 1 * 3) / 64; el x3 encadena y suma
        Assert.Equal(4, good.GetProperty("totalWeight").GetInt32());
        Assert.False(greedy.GetProperty("valid").GetBoolean());
        Assert.Contains("100%", greedy.GetProperty("error").GetString());
        var settings = await admin.GetFromJsonAsync<JsonElement>("/backoffice/games/slots/settings");
        Assert.Equal(before, settings.GetProperty("version").GetInt32()); // la prueba no guarda nada
    }

    [Fact]
    public async Task Publishing_creates_a_new_version_that_players_see_and_the_history_records_who_and_when()
    {
        using var app = StartApp();
        var adminId = Guid.NewGuid();
        using var admin = app.ClientFor(adminId, Roles.Backoffice);
        using var player = app.ClientFor(Guid.NewGuid());
        var current = (await admin.GetFromJsonAsync<JsonElement>("/backoffice/games/slots/settings")).GetProperty("version").GetInt32();

        var saved = await JsonAsync(await PutAsync(admin, Body(Fair, maxStake: 777, baseVersion: current)));

        Assert.Equal(current + 1, saved.GetProperty("version").GetInt32());
        var table = await player.GetFromJsonAsync<JsonElement>("/games/slots/paytable");
        Assert.Equal(2, table.GetProperty("symbols").GetArrayLength());
        Assert.Equal(777, table.GetProperty("maxStake").GetInt64());
        var history = await admin.GetFromJsonAsync<JsonElement>("/backoffice/games/slots/settings/history");
        var latest = history[0];
        Assert.Equal(current + 1, latest.GetProperty("version").GetInt32());
        Assert.Equal(adminId, latest.GetProperty("changedBy").GetGuid());
        Assert.True(DateTimeOffset.UtcNow - latest.GetProperty("changedAt").GetDateTimeOffset() < TimeSpan.FromMinutes(1));
        Assert.InRange(latest.GetProperty("returnToPlayerPercent").GetDouble(), 48.3, 48.5);
    }

    [Fact]
    public async Task An_invalid_table_is_refused_with_the_reason_and_the_current_one_stays()
    {
        using var app = StartApp();
        using var admin = app.ClientFor(Guid.NewGuid(), Roles.Backoffice);
        var before = (await admin.GetFromJsonAsync<JsonElement>("/backoffice/games/slots/settings")).GetProperty("version").GetInt32();

        var tooGenerous = await PutAsync(admin, Body(Symbols(("A", 1, 50), ("B", 1, 50)), baseVersion: before));
        var oneSymbol = await PutAsync(admin, Body(Symbols(("A", 1, 1)), baseVersion: before));
        var noName = await PutAsync(admin, Body(Symbols((" ", 1, 1), ("B", 1, 1)), baseVersion: before));
        var badStake = await PutAsync(admin, Body(Fair, maxStake: 0, baseVersion: before));

        Assert.All(new[] { tooGenerous, oneSymbol, noName, badStake }, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Contains("100%", await tooGenerous.Content.ReadAsStringAsync());
        Assert.Equal(before, (await admin.GetFromJsonAsync<JsonElement>("/backoffice/games/slots/settings")).GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Publishing_on_top_of_a_stale_version_is_a_conflict_instead_of_overwriting_someone_elses_change()
    {
        using var app = StartApp();
        using var admin = app.ClientFor(Guid.NewGuid(), Roles.Backoffice);
        var seen = (await admin.GetFromJsonAsync<JsonElement>("/backoffice/games/slots/settings")).GetProperty("version").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(admin, Body(Fair, baseVersion: seen))).StatusCode); // otra persona publica primero

        var stale = await PutAsync(admin, Body(Symbols(("X", 1, 1), ("Y", 1, 1)), baseVersion: seen));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var now = await admin.GetFromJsonAsync<JsonElement>("/backoffice/games/slots/settings");
        Assert.Equal("A", now.GetProperty("current").GetProperty("symbols")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Two_admins_publishing_at_the_same_moment_end_with_one_winner_and_one_conflict()
    {
        using var app = StartApp();
        using var one = app.ClientFor(Guid.NewGuid(), Roles.Backoffice);
        using var two = app.ClientFor(Guid.NewGuid(), Roles.Backoffice);
        var seen = (await one.GetFromJsonAsync<JsonElement>("/backoffice/games/slots/settings")).GetProperty("version").GetInt32();

        var results = await Task.WhenAll(PutAsync(one, Body(Fair, baseVersion: seen)), PutAsync(two, Body(Fair, maxStake: 123, baseVersion: seen)));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task A_spin_uses_the_published_table_and_records_its_version_so_it_can_be_recalculated_later()
    {
        using var app = StartApp();
        using var admin = app.ClientFor(Guid.NewGuid(), Roles.Backoffice);
        var seen = (await admin.GetFromJsonAsync<JsonElement>("/backoffice/games/slots/settings")).GetProperty("version").GetInt32();
        var saved = await JsonAsync(await PutAsync(admin, Body(Fair, maxStake: 50, baseVersion: seen)));
        var version = saved.GetProperty("version").GetInt32();

        var userId = Guid.NewGuid();
        var wallet = app.Services.GetRequiredService<WalletService>();
        await wallet.CreditAsync(await wallet.OpenAccountAsync(userId), "initial", 1_000);
        using var player = app.ClientFor(userId);

        // El tope nuevo (50) se aplica ya: 100 se rechaza, 50 pasa.
        Assert.Equal(HttpStatusCode.BadRequest, (await SpinAsync(player, 100, "over")).StatusCode);
        var accepted = await JsonAsync(await SpinAsync(player, 50, "ok"));
        var betId = accepted.GetProperty("betId").GetGuid();
        JsonElement spin = default;
        await WaitUntilAsync(async () =>
        {
            spin = await player.GetFromJsonAsync<JsonElement>($"/games/slots/spins/{betId}");
            return spin.GetProperty("status").GetString() == "Settled";
        });

        Assert.Equal(version, spin.GetProperty("paytableVersion").GetInt32());
        Assert.All(spin.GetProperty("reels").EnumerateArray(), r => Assert.Contains(r.GetString(), NewSymbols)); // solo existen los simbolos nuevos
        var table = (await app.Services.GetRequiredService<SlotsSettingsStore>().GetVersionAsync(version))!.Paytable;
        Assert.Equal(2, table.Symbols.Count); // la version guardada permite recalcular el giro
        Assert.Equal(0, (await app.Services.GetRequiredService<SlotsSettingsStore>().GetVersionAsync(0))!.Version);
        Assert.Null(await app.Services.GetRequiredService<SlotsSettingsStore>().GetVersionAsync(999));
    }

    private static async Task<HttpResponseMessage> SpinAsync(HttpClient client, long stake, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/games/slots/spins") { Content = JsonContent.Create(new { stake }) };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (Exception)
            {
                // Todavia no esta listo.
            }

            await Task.Delay(300);
        }

        throw new TimeoutException("El giro no llego a Settled.");
    }
}
