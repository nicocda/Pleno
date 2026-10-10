using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casino.BuildingBlocks;
using Casino.Modules.Wallet.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Casino.Integration.Tests.Wallet;

/// <summary>
/// Los retiros de punta a punta con Postgres real: el jugador aparta fichas en una reserva, su cajero las cobra (pasan a su cuenta en una sola transaccion), las rechaza, el jugador cancela o
/// la reserva vence. Un jugador sin cajero (o un jefe) lo atiende el backoffice y las fichas vuelven a la casa. Las fichas nunca desaparecen ni se duplican.
/// </summary>
[Collection(WalletDbDefinition.Name)]
public sealed class WithdrawalFlowTests : IDisposable
{
    private readonly CasinoCluster _app;
    private readonly WalletService _wallet;

    public WithdrawalFlowTests(PostgresFixture db)
    {
        _app = TestAuth.StartApp(db);
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

    private async Task<(long Available, long Reserved)> BalanceAsync(Guid userId)
    {
        var account = await _wallet.GetAsync(PlayerIds.WalletAccountFor(userId));
        return (account.Available, account.Reserved);
    }

    private static Task<HttpResponseMessage> PlaceAsync(HttpClient client, string level, Guid userId, Guid? parent) =>
        client.PutAsJsonAsync($"/backoffice/wallet/hierarchy/{userId}", new { level, parentUserId = parent });

    private static Task<HttpResponseMessage> RequestAsync(HttpClient client, long amount, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/wallet/withdrawals") { Content = JsonContent.Create(new { amount }) };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return client.SendAsync(request);
    }

    private static async Task<Guid> RequestedAsync(HttpClient client, long amount, string key)
    {
        var response = await RequestAsync(client, amount, key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<string> StatusOfAsync(HttpClient player, Guid id) =>
        (await player.GetFromJsonAsync<JsonElement>("/wallet/withdrawals")).EnumerateArray().Single(w => w.GetProperty("id").GetGuid() == id).GetProperty("status").GetString()!;

    /// <summary>Un jefe, un cajero y un jugador con fichas, ya ubicados en el arbol.</summary>
    private async Task<(Guid Head, Guid Cashier, Guid Player)> TreeAsync(long playerChips = 1_000)
    {
        var (head, cashier, player) = (await UserAsync(), await UserAsync(), await UserAsync(playerChips));
        using var backoffice = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);
        await PlaceAsync(backoffice, Roles.HeadCashier, head, null);
        await PlaceAsync(backoffice, Roles.Cashier, cashier, head);
        await PlaceAsync(backoffice, Roles.Player, player, cashier);
        return (head, cashier, player);
    }

    [Fact]
    public async Task A_withdrawal_request_sets_the_chips_aside_and_waits_for_the_cashier()
    {
        var (_, cashier, player) = await TreeAsync();
        using var playerClient = _app.ClientFor(player);

        var id = await RequestedAsync(playerClient, 300, "w-1");

        Assert.Equal((700L, 300L), await BalanceAsync(player)); // apartadas: no se pueden jugar
        Assert.Equal("Pending", await StatusOfAsync(playerClient, id));
        using var cashierClient = _app.ClientFor(cashier, Roles.Player, Roles.Cashier);
        var pending = await cashierClient.GetFromJsonAsync<JsonElement>("/cashier/withdrawals");
        var mine = Assert.Single(pending.EnumerateArray());
        Assert.Equal((id, 300L), (mine.GetProperty("id").GetGuid(), mine.GetProperty("amount").GetInt64()));
    }

    [Fact]
    public async Task The_cashier_collects_the_withdrawal_and_the_chips_move_to_their_account_without_the_total_changing()
    {
        var (_, cashier, player) = await TreeAsync();
        await _wallet.CreditAsync(PlayerIds.WalletAccountFor(cashier), "float", 50);
        using var playerClient = _app.ClientFor(player);
        using var cashierClient = _app.ClientFor(cashier, Roles.Player, Roles.Cashier);
        var id = await RequestedAsync(playerClient, 300, "w-1");

        var paid = await cashierClient.PostAsync($"/cashier/withdrawals/{id}/pay", content: null);

        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        Assert.Equal("Paid", (await paid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal((700L, 0L), await BalanceAsync(player)); // la reserva se cerro
        Assert.Equal((350L, 0L), await BalanceAsync(cashier)); // 50 + 300
        Assert.Equal("Paid", await StatusOfAsync(playerClient, id));
        Assert.Empty((await cashierClient.GetFromJsonAsync<JsonElement>("/cashier/withdrawals")).EnumerateArray());
    }

    [Fact]
    public async Task Collecting_twice_pays_once()
    {
        var (_, cashier, player) = await TreeAsync();
        using var playerClient = _app.ClientFor(player);
        using var cashierClient = _app.ClientFor(cashier, Roles.Player, Roles.Cashier);
        var id = await RequestedAsync(playerClient, 300, "w-1");

        var first = await cashierClient.PostAsync($"/cashier/withdrawals/{id}/pay", content: null);
        var again = await cashierClient.PostAsync($"/cashier/withdrawals/{id}/pay", content: null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal((300L, 0L), await BalanceAsync(cashier)); // una sola vez
    }

    [Fact]
    public async Task A_request_without_enough_chips_or_below_the_minimum_is_refused_and_sets_nothing_aside()
    {
        var (_, _, player) = await TreeAsync(playerChips: 100);
        using var playerClient = _app.ClientFor(player);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await RequestAsync(playerClient, 101, "a-1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await RequestAsync(playerClient, 5, "a-2")).StatusCode); // el minimo es 10
        Assert.Equal(HttpStatusCode.BadRequest, (await RequestAsync(playerClient, 50, null)).StatusCode); // sin Idempotency-Key

        Assert.Equal((100L, 0L), await BalanceAsync(player));
    }

    [Fact]
    public async Task Repeating_a_request_with_the_same_key_is_the_same_request_and_another_amount_is_a_conflict()
    {
        var (_, _, player) = await TreeAsync();
        using var playerClient = _app.ClientFor(player);
        var id = await RequestedAsync(playerClient, 300, "same");

        var again = await RequestedAsync(playerClient, 300, "same");

        Assert.Equal(id, again);
        Assert.Equal((700L, 300L), await BalanceAsync(player)); // una sola reserva
        Assert.Equal(HttpStatusCode.Conflict, (await RequestAsync(playerClient, 400, "same")).StatusCode);
    }

    [Fact]
    public async Task The_player_can_cancel_a_pending_request_and_gets_the_chips_back_but_not_after_it_was_paid()
    {
        var (_, cashier, player) = await TreeAsync();
        using var playerClient = _app.ClientFor(player);
        using var cashierClient = _app.ClientFor(cashier, Roles.Player, Roles.Cashier);
        var cancelled = await RequestedAsync(playerClient, 300, "w-1");
        var paid = await RequestedAsync(playerClient, 200, "w-2");
        await cashierClient.PostAsync($"/cashier/withdrawals/{paid}/pay", content: null);

        Assert.Equal(HttpStatusCode.OK, (await playerClient.PostAsync($"/wallet/withdrawals/{cancelled}/cancel", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await playerClient.PostAsync($"/wallet/withdrawals/{paid}/cancel", content: null)).StatusCode);
        // Y el cajero ya no puede cobrar el cancelado.
        Assert.Equal(HttpStatusCode.Conflict, (await cashierClient.PostAsync($"/cashier/withdrawals/{cancelled}/pay", content: null)).StatusCode);

        Assert.Equal((800L, 0L), await BalanceAsync(player)); // 1.000 - 200 cobrados; los 300 cancelados volvieron
        Assert.Equal("Cancelled", await StatusOfAsync(playerClient, cancelled));
    }

    [Fact]
    public async Task The_cashier_can_reject_and_the_chips_go_back_to_the_player()
    {
        var (_, cashier, player) = await TreeAsync();
        using var playerClient = _app.ClientFor(player);
        using var cashierClient = _app.ClientFor(cashier, Roles.Player, Roles.Cashier);
        var id = await RequestedAsync(playerClient, 300, "w-1");

        Assert.Equal(HttpStatusCode.OK, (await cashierClient.PostAsync($"/cashier/withdrawals/{id}/reject", content: null)).StatusCode);

        Assert.Equal((1_000L, 0L), await BalanceAsync(player));
        Assert.Equal((0L, 0L), await BalanceAsync(cashier));
        Assert.Equal("Rejected", await StatusOfAsync(playerClient, id));
    }

    [Fact]
    public async Task Only_the_cashier_of_that_player_can_collect_or_reject_and_a_stranger_cannot_cancel_it()
    {
        var (head, cashier, player) = await TreeAsync();
        var otherCashier = await UserAsync();
        using var backoffice = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);
        await PlaceAsync(backoffice, Roles.Cashier, otherCashier, head);
        using var playerClient = _app.ClientFor(player);
        using var otherPlayer = _app.ClientFor(await UserAsync());
        using var otherCashierClient = _app.ClientFor(otherCashier, Roles.Player, Roles.Cashier);
        using var headClient = _app.ClientFor(head, Roles.Player, Roles.HeadCashier);
        var id = await RequestedAsync(playerClient, 300, "w-1");

        Assert.Equal(HttpStatusCode.Forbidden, (await otherCashierClient.PostAsync($"/cashier/withdrawals/{id}/pay", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await otherCashierClient.PostAsync($"/cashier/withdrawals/{id}/reject", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await headClient.PostAsync($"/cashier/withdrawals/{id}/pay", content: null)).StatusCode); // ni el jefe: es del cajero directo
        Assert.Equal(HttpStatusCode.NotFound, (await otherPlayer.PostAsync($"/wallet/withdrawals/{id}/cancel", content: null)).StatusCode);
        Assert.Empty((await otherCashierClient.GetFromJsonAsync<JsonElement>("/cashier/withdrawals")).EnumerateArray());

        Assert.Equal((700L, 300L), await BalanceAsync(player));
    }

    [Fact]
    public async Task A_player_cannot_use_the_cashier_withdrawal_endpoints()
    {
        var (_, _, player) = await TreeAsync();
        using var playerClient = _app.ClientFor(player);
        var id = await RequestedAsync(playerClient, 300, "w-1");

        Assert.Equal(HttpStatusCode.Forbidden, (await playerClient.GetAsync("/cashier/withdrawals")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await playerClient.PostAsync($"/cashier/withdrawals/{id}/pay", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await playerClient.GetAsync("/backoffice/wallet/withdrawals")).StatusCode);
    }

    [Fact]
    public async Task A_request_nobody_attends_expires_when_the_wallet_releases_the_reservation_and_can_no_longer_be_collected()
    {
        var (_, cashier, player) = await TreeAsync();
        using var playerClient = _app.ClientFor(player);
        using var cashierClient = _app.ClientFor(cashier, Roles.Player, Roles.Cashier);
        var id = await RequestedAsync(playerClient, 300, "w-1");

        // Lo que hace el mensaje programado de vencimiento de la Wallet al cumplirse el plazo.
        await _wallet.ReleaseAsync(PlayerIds.WalletAccountFor(player), $"expire:{id:N}", id);

        Assert.Equal("Expired", await StatusOfAsync(playerClient, id));
        Assert.Equal(HttpStatusCode.Conflict, (await cashierClient.PostAsync($"/cashier/withdrawals/{id}/pay", content: null)).StatusCode);
        Assert.Equal((1_000L, 0L), await BalanceAsync(player));
        Assert.Equal((0L, 0L), await BalanceAsync(cashier));
    }

    [Fact]
    public async Task A_player_without_a_cashier_and_a_head_of_cashiers_are_attended_by_the_backoffice_and_the_chips_go_back_to_the_house()
    {
        var (head, cashier, _) = await TreeAsync();
        var loner = await UserAsync(500);
        await _wallet.CreditAsync(PlayerIds.WalletAccountFor(head), "float", 400);
        using var backoffice = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);
        using var lonerClient = _app.ClientFor(loner);
        using var headPlayerClient = _app.ClientFor(head, Roles.Player, Roles.HeadCashier);
        using var cashierClient = _app.ClientFor(cashier, Roles.Player, Roles.Cashier);
        var lonerId = await RequestedAsync(lonerClient, 200, "w-1");
        var headId = await RequestedAsync(headPlayerClient, 100, "w-2");

        var pending = (await backoffice.GetFromJsonAsync<JsonElement>("/backoffice/wallet/withdrawals")).EnumerateArray().Select(w => w.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(lonerId, pending);
        Assert.Contains(headId, pending);
        Assert.Empty((await cashierClient.GetFromJsonAsync<JsonElement>("/cashier/withdrawals")).EnumerateArray()); // un cajero no los ve
        Assert.Equal(HttpStatusCode.Forbidden, (await cashierClient.PostAsync($"/cashier/withdrawals/{lonerId}/pay", content: null)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await backoffice.PostAsync($"/backoffice/wallet/withdrawals/{lonerId}/pay", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await backoffice.PostAsync($"/backoffice/wallet/withdrawals/{headId}/reject", content: null)).StatusCode);

        Assert.Equal((300L, 0L), await BalanceAsync(loner)); // 500 - 200 devueltos a la casa
        Assert.Equal((400L, 0L), await BalanceAsync(head)); // rechazado: volvio
        Assert.Equal("Paid", await StatusOfAsync(lonerClient, lonerId));
    }

    [Fact]
    public async Task The_backoffice_cannot_take_a_withdrawal_that_belongs_to_a_cashier()
    {
        var (_, _, player) = await TreeAsync();
        using var backoffice = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);
        using var playerClient = _app.ClientFor(player);
        var id = await RequestedAsync(playerClient, 300, "w-1");

        Assert.Equal(HttpStatusCode.Forbidden, (await backoffice.PostAsync($"/backoffice/wallet/withdrawals/{id}/pay", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await backoffice.PostAsync($"/backoffice/wallet/withdrawals/{id}/reject", content: null)).StatusCode);
        Assert.Equal((700L, 300L), await BalanceAsync(player));
    }

    [Fact]
    public async Task Cancelling_and_collecting_at_the_same_instant_leave_exactly_one_winner_and_the_chips_add_up()
    {
        var (_, cashier, player) = await TreeAsync();
        using var playerClient = _app.ClientFor(player);
        using var cashierClient = _app.ClientFor(cashier, Roles.Player, Roles.Cashier);
        var id = await RequestedAsync(playerClient, 300, "w-1");

        var results = await Task.WhenAll(
            playerClient.PostAsync($"/wallet/withdrawals/{id}/cancel", content: null),
            cashierClient.PostAsync($"/cashier/withdrawals/{id}/pay", content: null));

        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK); // uno gana
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict); // el otro llega tarde
        var (playerAvailable, playerReserved) = await BalanceAsync(player);
        var (cashierAvailable, _) = await BalanceAsync(cashier);
        Assert.Equal(0, playerReserved);
        Assert.Equal(1_000, playerAvailable + cashierAvailable); // ni se pierden ni se duplican
    }

    [Fact]
    public async Task The_statement_shows_the_withdrawal_and_its_return_as_their_own_concepts()
    {
        var (_, cashier, player) = await TreeAsync();
        using var playerClient = _app.ClientFor(player);
        using var cashierClient = _app.ClientFor(cashier, Roles.Player, Roles.Cashier);
        var requested = await RequestedAsync(playerClient, 300, "w-1");
        await cashierClient.PostAsync($"/cashier/withdrawals/{requested}/reject", content: null);

        var playerMovements = (await _wallet.GetMovementsAsync(PlayerIds.WalletAccountFor(player), 10, null)).Items;
        await RequestedAsync(playerClient, 200, "w-2");
        var paidId = (await playerClient.GetFromJsonAsync<JsonElement>("/wallet/withdrawals")).EnumerateArray().First(w => w.GetProperty("status").GetString() == "Pending").GetProperty("id").GetGuid();
        await cashierClient.PostAsync($"/cashier/withdrawals/{paidId}/pay", content: null);
        var cashierMovements = (await _wallet.GetMovementsAsync(PlayerIds.WalletAccountFor(cashier), 10, null)).Items;

        Assert.Contains(playerMovements, m => m is { Kind: MovementKind.Withdrawal, Delta: -300 });
        Assert.Contains(playerMovements, m => m is { Kind: MovementKind.WithdrawalReturned, Delta: 300 });
        Assert.Contains(cashierMovements, m => m is { Kind: MovementKind.TransferIn, Delta: 200 });
    }
}
