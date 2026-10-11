using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casino.BuildingBlocks;
using Casino.Modules.Wallet.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Casino.Integration.Tests.Wallet;

/// <summary>
/// La comision del cajero de punta a punta con Postgres real: la paga la casa (emite fichas, acotadas), se acredita a quien cargo, no se paga dos veces aunque se repita la carga
/// y respeta el tope por carga y por dia. Topes bajos por configuracion para poder verlos.
/// </summary>
[Collection(WalletDbDefinition.Name)]
public sealed class CashierCommissionFlowTests : IDisposable
{
    private readonly CasinoCluster _app;
    private readonly WalletService _wallet;

    public CashierCommissionFlowTests(PostgresFixture db)
    {
        _app = TestAuth.StartApp(db, customize: builder =>
        {
            builder.UseSetting("Cashiers:MaxCommissionPerLoad", "15");
            builder.UseSetting("Cashiers:MaxCommissionPerDay", "40");
            builder.UseSetting("Cashiers:HeadCashierCommissionPermille", "10");
        });
        _wallet = _app.Services.GetRequiredService<WalletService>();
    }

    public void Dispose() => _app.Dispose();

    private async Task<Guid> UserAsync(long chips = 0)
    {
        var userId = Guid.NewGuid();
        var accountId = await _wallet.OpenAccountAsync(userId);
        if (chips > 0)
        {
            await _wallet.CreditAsync(accountId, "initial", chips);
        }

        return userId;
    }

    private async Task<long> AvailableAsync(Guid userId) => (await _wallet.GetAsync(PlayerIds.WalletAccountFor(userId))).Available;

    private static async Task<JsonElement> LoadAsync(HttpClient client, Guid toUserId, long amount, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/cashier/transfers") { Content = JsonContent.Create(new { toUserId, amount }) };
        request.Headers.Add("Idempotency-Key", key);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Un jefe, un cajero con muchas fichas y un jugador, ya ubicados en el arbol.</summary>
    private async Task<(Guid Head, Guid Cashier, Guid Player)> TreeAsync(long cashierChips = 100_000, long headChips = 100_000)
    {
        var (head, cashier, player) = (await UserAsync(headChips), await UserAsync(cashierChips), await UserAsync());
        using var backoffice = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);
        await backoffice.PutAsJsonAsync($"/backoffice/wallet/hierarchy/{head}", new { level = Roles.HeadCashier, parentUserId = (Guid?)null });
        await backoffice.PutAsJsonAsync($"/backoffice/wallet/hierarchy/{cashier}", new { level = Roles.Cashier, parentUserId = head });
        await backoffice.PutAsJsonAsync($"/backoffice/wallet/hierarchy/{player}", new { level = Roles.Player, parentUserId = cashier });
        return (head, cashier, player);
    }

    [Fact]
    public async Task A_load_pays_the_cashier_a_commission_that_the_house_issues_and_the_player_gets_the_full_amount()
    {
        var (_, cashier, player) = await TreeAsync();
        using var client = _app.ClientFor(cashier, Roles.Player, Roles.Cashier);

        var result = await LoadAsync(client, player, 500, "c-1"); // 2 % de 500 = 10 (bajo el tope de 15)

        Assert.Equal(10, result.GetProperty("commission").GetInt64());
        Assert.Equal(100_000 - 500 + 10, await AvailableAsync(cashier)); // sale la carga y entra la comision
        Assert.Equal(500, await AvailableAsync(player)); // el jugador recibe todo
    }

    [Fact]
    public async Task Repeating_the_same_load_pays_the_commission_only_once()
    {
        var (_, cashier, player) = await TreeAsync();
        using var client = _app.ClientFor(cashier, Roles.Player, Roles.Cashier);

        var first = await LoadAsync(client, player, 500, "c-1");
        var again = await LoadAsync(client, player, 500, "c-1");

        Assert.True(again.GetProperty("isDuplicate").GetBoolean());
        Assert.Equal(first.GetProperty("commission").GetInt64(), again.GetProperty("commission").GetInt64());
        Assert.Equal(100_000 - 500 + 10, await AvailableAsync(cashier));
        Assert.Equal(500, await AvailableAsync(player));
    }

    [Fact]
    public async Task The_commission_respects_the_per_load_cap_and_the_daily_cap_and_a_retry_pays_what_was_decided()
    {
        var (_, cashier, player) = await TreeAsync();
        using var client = _app.ClientFor(cashier, Roles.Player, Roles.Cashier);

        var one = await LoadAsync(client, player, 1_000, "d-1"); // 2 % = 20, tope por carga 15
        var two = await LoadAsync(client, player, 1_000, "d-2"); // 15 (total 30)
        var three = await LoadAsync(client, player, 1_000, "d-3"); // quedan 10 del tope diario de 40
        var four = await LoadAsync(client, player, 1_000, "d-4"); // diario agotado: 0
        var retryThree = await LoadAsync(client, player, 1_000, "d-3"); // reintento de la tercera: lo mismo, no vuelve a calcular con el tope ya agotado

        Assert.Equal([15L, 15L, 10L, 0L], new[] { one, two, three, four }.Select(r => r.GetProperty("commission").GetInt64()));
        Assert.Equal(10, retryThree.GetProperty("commission").GetInt64());
        Assert.Equal(100_000 - 4_000 + 40, await AvailableAsync(cashier));
    }

    [Fact]
    public async Task A_very_small_load_pays_no_commission_and_still_goes_through()
    {
        var (_, cashier, player) = await TreeAsync();
        using var client = _app.ClientFor(cashier, Roles.Player, Roles.Cashier);

        var result = await LoadAsync(client, player, 40, "s-1"); // 2 % de 40 = 0,8 -> 0

        Assert.Equal(0, result.GetProperty("commission").GetInt64());
        Assert.Equal(100_000 - 40, await AvailableAsync(cashier));
        Assert.Equal(40, await AvailableAsync(player));
    }

    [Fact]
    public async Task The_head_of_cashiers_earns_the_rate_set_for_heads_when_loading_a_cashier()
    {
        var (head, cashier, _) = await TreeAsync();
        using var client = _app.ClientFor(head, Roles.Player, Roles.HeadCashier);

        var result = await LoadAsync(client, cashier, 1_000, "h-1"); // los jefes cobran 1 % (por configuracion de esta prueba) = 10

        Assert.Equal(10, result.GetProperty("commission").GetInt64());
        Assert.Equal(100_000 - 1_000 + 10, await AvailableAsync(head));
    }

    [Fact]
    public async Task The_movements_statement_shows_the_commission_as_its_own_concept_and_the_backoffice_sees_what_the_house_paid()
    {
        var (_, cashier, player) = await TreeAsync();
        using var client = _app.ClientFor(cashier, Roles.Player, Roles.Cashier);
        await LoadAsync(client, player, 500, "m-1");
        using var backoffice = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);

        var movements = (await _wallet.GetMovementsAsync(PlayerIds.WalletAccountFor(cashier), 10, null)).Items;
        var commissions = await backoffice.GetFromJsonAsync<JsonElement>("/backoffice/wallet/commissions?limit=200");

        Assert.Contains(movements, m => m is { Kind: MovementKind.Commission, Delta: 10 });
        Assert.Contains(movements, m => m is { Kind: MovementKind.TransferOut, Delta: -500 });
        var mine = commissions.GetProperty("items").EnumerateArray().Single(c => c.GetProperty("actorUserId").GetGuid() == cashier);
        Assert.Equal((500L, 10L), (mine.GetProperty("loadedAmount").GetInt64(), mine.GetProperty("amount").GetInt64()));
        Assert.True(commissions.GetProperty("totalPaid").GetInt64() >= 10);
    }

    [Fact]
    public async Task The_cashier_can_see_what_commission_they_earn()
    {
        var (_, cashier, _) = await TreeAsync();
        using var client = _app.ClientFor(cashier, Roles.Player, Roles.Cashier);

        var me = await client.GetFromJsonAsync<JsonElement>("/cashier/me");

        Assert.Equal(20, me.GetProperty("commissionPermille").GetInt32());
    }
}
