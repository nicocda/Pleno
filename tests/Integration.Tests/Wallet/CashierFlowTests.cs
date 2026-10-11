using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casino.BuildingBlocks;
using Casino.Modules.Wallet.Application;
using Casino.Modules.Wallet.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Casino.Integration.Tests.Wallet;

/// <summary>
/// Cajeros y jefes de cajeros de punta a punta con Postgres real: la transferencia entre cuentas es atomica e idempotente, el total de fichas no cambia nunca
/// y cada uno solo carga hacia abajo y dentro de su jurisdiccion (jefe → cajero → jugador), desde su propio saldo.
/// </summary>
[Collection(WalletDbDefinition.Name)]
public sealed class CashierFlowTests : IDisposable
{
    private readonly CasinoCluster _app;
    private readonly WalletService _wallet;

    public CashierFlowTests(PostgresFixture db)
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

    private static Task<HttpResponseMessage> AssignAsync(HttpClient backoffice, Guid userId, string level, Guid? parent) =>
        backoffice.PutAsJsonAsync($"/backoffice/wallet/hierarchy/{userId}", new { level, parentUserId = parent });

    private static Task<HttpResponseMessage> LoadAsync(HttpClient client, Guid toUserId, long amount, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/cashier/transfers") { Content = JsonContent.Create(new { toUserId, amount }) };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return client.SendAsync(request);
    }

    // ---- La transferencia (Wallet) ----

    [Fact]
    public async Task A_transfer_moves_the_chips_from_one_account_to_the_other_and_the_total_stays_the_same()
    {
        var (from, to) = (await UserAsync(1_000), await UserAsync(50));

        var outcome = await _wallet.TransferAsync(from, to, "t-1", 300);

        Assert.False(outcome.IsDuplicate);
        Assert.Equal((700L, 0L), await BalanceAsync(from));
        Assert.Equal((350L, 0L), await BalanceAsync(to));
    }

    [Fact]
    public async Task Repeating_a_transfer_moves_nothing_again_and_reusing_the_key_with_other_content_is_refused()
    {
        var (from, to) = (await UserAsync(1_000), await UserAsync());
        var first = await _wallet.TransferAsync(from, to, "t-1", 100);

        var again = await _wallet.TransferAsync(from, to, "t-1", 100);

        Assert.True(again.IsDuplicate);
        Assert.Equal(first.TransactionId, again.TransactionId);
        Assert.Equal((900L, 0L), await BalanceAsync(from));
        Assert.Equal((100L, 0L), await BalanceAsync(to));
        var error = await Assert.ThrowsAsync<WalletDomainException>(() => _wallet.TransferAsync(from, to, "t-1", 200));
        Assert.Equal(WalletError.IdempotencyKeyReused, error.Error);
    }

    [Fact]
    public async Task A_transfer_without_enough_chips_fails_and_touches_neither_account()
    {
        var (from, to) = (await UserAsync(100), await UserAsync(10));

        var error = await Assert.ThrowsAsync<WalletDomainException>(() => _wallet.TransferAsync(from, to, "t-1", 101));

        Assert.Equal(WalletError.InsufficientFunds, error.Error);
        Assert.Equal((100L, 0L), await BalanceAsync(from));
        Assert.Equal((10L, 0L), await BalanceAsync(to));
    }

    [Fact]
    public async Task A_transfer_to_an_account_that_does_not_exist_fails_without_taking_chips()
    {
        var from = await UserAsync(100);

        var error = await Assert.ThrowsAsync<WalletDomainException>(() => _wallet.TransferAsync(from, Guid.NewGuid(), "t-1", 10));

        Assert.Equal(WalletError.AccountNotFound, error.Error);
        Assert.Equal((100L, 0L), await BalanceAsync(from));
    }

    [Fact]
    public async Task The_same_key_from_a_cashier_does_not_clash_with_an_unrelated_operation_of_the_receiver()
    {
        var (from, to) = (await UserAsync(1_000), await UserAsync());
        await _wallet.CreditAsync(PlayerIds.WalletAccountFor(to), "shared-key", 5); // la misma clave, otra operacion, en la cuenta que recibe

        await _wallet.TransferAsync(from, to, "shared-key", 100);

        Assert.Equal((105L, 0L), await BalanceAsync(to));
    }

    [Fact]
    public async Task Many_transfers_in_both_directions_at_once_lose_and_duplicate_nothing()
    {
        var (a, b) = (await UserAsync(1_000), await UserAsync(1_000));

        var work = Enumerable.Range(0, 15).Select(i => _wallet.TransferAsync(a, b, $"ab-{i}", 10))
            .Concat(Enumerable.Range(0, 15).Select(i => _wallet.TransferAsync(b, a, $"ba-{i}", 7)));
        await Task.WhenAll(work);

        var (availableA, _) = await BalanceAsync(a);
        var (availableB, _) = await BalanceAsync(b);
        Assert.Equal(2_000, availableA + availableB);
        Assert.Equal((1_000 - 150 + 105, 1_000 + 150 - 105), (availableA, availableB));
    }

    [Fact]
    public async Task The_movements_statement_shows_the_transfer_in_and_out()
    {
        var (from, to) = (await UserAsync(1_000), await UserAsync());
        await _wallet.TransferAsync(from, to, "t-1", 300);

        var sent = (await _wallet.GetMovementsAsync(PlayerIds.WalletAccountFor(from), 10, null)).Items[0];
        var received = (await _wallet.GetMovementsAsync(PlayerIds.WalletAccountFor(to), 10, null)).Items[0];

        Assert.Equal((MovementKind.TransferOut, -300L, 700L), (sent.Kind, sent.Delta, sent.BalanceAfter));
        Assert.Equal((MovementKind.TransferIn, 300L, 300L), (received.Kind, received.Delta, received.BalanceAfter));
    }

    // ---- El arbol y las cargas por la API ----

    [Fact]
    public async Task The_chain_head_to_cashier_to_player_moves_chips_only_downwards_and_from_each_ones_own_balance()
    {
        var (head, cashier, player) = (await UserAsync(), await UserAsync(), await UserAsync());
        using var backoffice = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);
        Assert.Equal(HttpStatusCode.OK, (await AssignAsync(backoffice, head, Roles.HeadCashier, null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AssignAsync(backoffice, cashier, Roles.Cashier, head)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AssignAsync(backoffice, player, Roles.Player, cashier)).StatusCode);
        // El backoffice le da fichas al jefe (como hoy a cualquiera); desde ahi solo se mueven, no se crean.
        await _wallet.CreditAsync(PlayerIds.WalletAccountFor(head), "from-backoffice", 1_000);
        using var headClient = _app.ClientFor(head, Roles.Player, Roles.HeadCashier);
        using var cashierClient = _app.ClientFor(cashier, Roles.Player, Roles.Cashier);

        Assert.Equal(HttpStatusCode.OK, (await LoadAsync(headClient, cashier, 400, "h-1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoadAsync(cashierClient, player, 150, "c-1")).StatusCode);

        Assert.Equal((600L, 0L), await BalanceAsync(head));
        Assert.Equal((253L, 0L), await BalanceAsync(cashier)); // 400 - 150 + 3 de comision (2 % de 150)
        Assert.Equal((150L, 0L), await BalanceAsync(player));
    }

    [Fact]
    public async Task A_head_can_only_load_cashiers_and_a_cashier_only_players_of_their_own_jurisdiction()
    {
        var (head, cashier, otherCashier, player, otherPlayer) = (await UserAsync(1_000), await UserAsync(1_000), await UserAsync(1_000), await UserAsync(), await UserAsync());
        using var backoffice = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);
        await AssignAsync(backoffice, head, Roles.HeadCashier, null);
        await AssignAsync(backoffice, cashier, Roles.Cashier, head);
        await AssignAsync(backoffice, otherCashier, Roles.Cashier, head);
        await AssignAsync(backoffice, player, Roles.Player, cashier);
        await AssignAsync(backoffice, otherPlayer, Roles.Player, otherCashier);
        using var headClient = _app.ClientFor(head, Roles.Player, Roles.HeadCashier);
        using var cashierClient = _app.ClientFor(cashier, Roles.Player, Roles.Cashier);

        // El jefe no le carga a un jugador directamente; el cajero no le carga a un jugador de otro cajero ni a otro cajero ni a su jefe.
        Assert.Equal(HttpStatusCode.Forbidden, (await LoadAsync(headClient, player, 10, "x-1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await LoadAsync(cashierClient, otherPlayer, 10, "x-2")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await LoadAsync(cashierClient, otherCashier, 10, "x-3")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await LoadAsync(cashierClient, head, 10, "x-4")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await LoadAsync(cashierClient, Guid.NewGuid(), 10, "x-5")).StatusCode); // alguien fuera del arbol

        Assert.Equal((1_000L, 0L), await BalanceAsync(cashier));
        Assert.Equal((0L, 0L), await BalanceAsync(player));
        Assert.Equal((0L, 0L), await BalanceAsync(otherPlayer));
    }

    [Fact]
    public async Task A_cashier_without_a_place_in_the_tree_or_with_another_level_cannot_load_anyone()
    {
        var (cashier, player) = (await UserAsync(1_000), await UserAsync());
        using var backoffice = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);
        await AssignAsync(backoffice, player, Roles.Player, null);
        using var cashierClient = _app.ClientFor(cashier, Roles.Player, Roles.Cashier);

        // Sin lugar en el arbol.
        Assert.Equal(HttpStatusCode.Forbidden, (await LoadAsync(cashierClient, player, 10, "x-1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cashierClient.GetAsync("/cashier/me")).StatusCode);

        // El rol del token dice cajero pero el backoffice lo puso como jugador: manda el arbol, no solo el rol.
        await AssignAsync(backoffice, cashier, Roles.Player, null);
        Assert.Equal(HttpStatusCode.Forbidden, (await LoadAsync(cashierClient, player, 10, "x-2")).StatusCode);
        Assert.Equal((1_000L, 0L), await BalanceAsync(cashier));
    }

    [Fact]
    public async Task A_player_or_a_backoffice_user_cannot_use_the_cashier_endpoints()
    {
        var player = await UserAsync();
        using var playerClient = _app.ClientFor(player);
        using var backoffice = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);

        Assert.Equal(HttpStatusCode.Forbidden, (await LoadAsync(playerClient, Guid.NewGuid(), 10, "x-1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await playerClient.GetAsync("/cashier/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await LoadAsync(backoffice, Guid.NewGuid(), 10, "x-2")).StatusCode);
        // Y la jerarquia la arma solo el backoffice.
        Assert.Equal(HttpStatusCode.Forbidden, (await AssignAsync(playerClient, player, Roles.Cashier, null)).StatusCode);
    }

    [Fact]
    public async Task A_load_needs_an_idempotency_key_and_repeating_it_does_not_load_twice()
    {
        var (cashier, player) = (await UserAsync(1_000), await UserAsync());
        using var backoffice = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);
        await AssignAsync(backoffice, cashier, Roles.Cashier, await HeadFor(backoffice));
        await AssignAsync(backoffice, player, Roles.Player, cashier);
        using var cashierClient = _app.ClientFor(cashier, Roles.Player, Roles.Cashier);

        Assert.Equal(HttpStatusCode.BadRequest, (await LoadAsync(cashierClient, player, 10, null)).StatusCode);
        var first = await (await LoadAsync(cashierClient, player, 100, "same")).Content.ReadFromJsonAsync<JsonElement>();
        var again = await (await LoadAsync(cashierClient, player, 100, "same")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(first.GetProperty("isDuplicate").GetBoolean());
        Assert.True(again.GetProperty("isDuplicate").GetBoolean());
        Assert.Equal((902L, 0L), await BalanceAsync(cashier)); // 1.000 - 100 + 2 de comision, una sola vez
        Assert.Equal((100L, 0L), await BalanceAsync(player));
        Assert.Equal(HttpStatusCode.Conflict, (await LoadAsync(cashierClient, player, 200, "same")).StatusCode); // otra cantidad con la misma clave
    }

    [Fact]
    public async Task Invalid_amounts_and_not_enough_chips_are_refused_and_move_nothing()
    {
        var (cashier, player) = (await UserAsync(100), await UserAsync());
        using var backoffice = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);
        await AssignAsync(backoffice, cashier, Roles.Cashier, await HeadFor(backoffice));
        await AssignAsync(backoffice, player, Roles.Player, cashier);
        using var cashierClient = _app.ClientFor(cashier, Roles.Player, Roles.Cashier);

        Assert.Equal(HttpStatusCode.BadRequest, (await LoadAsync(cashierClient, player, 0, "a-1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await LoadAsync(cashierClient, player, -5, "a-2")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await LoadAsync(cashierClient, player, 101, "a-3")).StatusCode);

        Assert.Equal((100L, 0L), await BalanceAsync(cashier));
        Assert.Equal((0L, 0L), await BalanceAsync(player));
    }

    [Fact]
    public async Task The_hierarchy_rules_are_enforced_when_the_backoffice_builds_the_tree()
    {
        var (head, cashier, player) = (await UserAsync(), await UserAsync(), await UserAsync());
        using var backoffice = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);
        await AssignAsync(backoffice, head, Roles.HeadCashier, null);
        await AssignAsync(backoffice, cashier, Roles.Cashier, head);

        Assert.Equal(HttpStatusCode.Conflict, (await AssignAsync(backoffice, head, Roles.HeadCashier, cashier)).StatusCode); // el jefe no tiene padre
        Assert.Equal(HttpStatusCode.Conflict, (await AssignAsync(backoffice, player, Roles.Player, head)).StatusCode); // un jugador depende de un cajero, no de un jefe
        Assert.Equal(HttpStatusCode.Conflict, (await AssignAsync(backoffice, cashier, Roles.Cashier, cashier)).StatusCode); // de si mismo
        Assert.Equal(HttpStatusCode.Conflict, (await AssignAsync(backoffice, cashier, Roles.Cashier, Guid.NewGuid())).StatusCode); // un padre que no existe
        Assert.Equal(HttpStatusCode.BadRequest, (await AssignAsync(backoffice, player, "superman", cashier)).StatusCode);
        // Con gente a cargo no se le cambia el nivel.
        await AssignAsync(backoffice, player, Roles.Player, cashier);
        Assert.Equal(HttpStatusCode.Conflict, (await AssignAsync(backoffice, cashier, Roles.Player, null)).StatusCode);

        var tree = await backoffice.GetFromJsonAsync<JsonElement>("/backoffice/wallet/hierarchy");
        var ids = tree.EnumerateArray().Select(n => n.GetProperty("userId").GetGuid()).ToList();
        Assert.Contains(head, ids);
        Assert.Contains(cashier, ids);
        Assert.Contains(player, ids);
    }

    [Fact]
    public async Task A_cashier_sees_their_players_with_balances_and_their_own_loads_and_the_audit_records_who_loaded_whom()
    {
        var (head, cashier, player, stranger) = (await UserAsync(), await UserAsync(1_000), await UserAsync(25), await UserAsync());
        using var backoffice = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);
        await AssignAsync(backoffice, head, Roles.HeadCashier, null);
        await AssignAsync(backoffice, cashier, Roles.Cashier, head);
        await AssignAsync(backoffice, player, Roles.Player, cashier);
        using var cashierClient = _app.ClientFor(cashier, Roles.Player, Roles.Cashier);
        await LoadAsync(cashierClient, player, 75, "l-1");

        var members = await cashierClient.GetFromJsonAsync<JsonElement>("/cashier/members");
        var mine = await cashierClient.GetFromJsonAsync<JsonElement>("/cashier/transfers");
        var audit = (await backoffice.GetFromJsonAsync<JsonElement>("/backoffice/wallet/audit?limit=200")).EnumerateArray()
            .Where(e => e.GetProperty("targetUserId").GetGuid() == player).ToList();

        var member = Assert.Single(members.EnumerateArray());
        Assert.Equal((player, Roles.Player, 100L), (member.GetProperty("userId").GetGuid(), member.GetProperty("level").GetString(), member.GetProperty("available").GetInt64()));
        Assert.DoesNotContain(members.EnumerateArray(), m => m.GetProperty("userId").GetGuid() == stranger);
        var sent = Assert.Single(mine.EnumerateArray());
        Assert.Equal((player, 75L), (sent.GetProperty("targetUserId").GetGuid(), sent.GetProperty("amount").GetInt64()));
        var entry = Assert.Single(audit);
        Assert.Equal(("ChipsTransferred", cashier, 75L), (entry.GetProperty("action").GetString(), entry.GetProperty("actorUserId").GetGuid(), entry.GetProperty("amount").GetInt64()));
    }

    // ---- Nombres: el cajero distingue a su gente, y solo a su gente ----

    [Fact]
    public async Task A_cashier_sees_the_user_names_of_their_players_and_nobody_elses()
    {
        var (head, cashier, otherCashier, mine, theirs, outsider) = (await UserAsync(), await UserAsync(), await UserAsync(), await UserAsync(), await UserAsync(), await UserAsync());
        using var backoffice = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);
        await AssignAsync(backoffice, head, Roles.HeadCashier, null);
        await AssignAsync(backoffice, cashier, Roles.Cashier, head);
        await AssignAsync(backoffice, otherCashier, Roles.Cashier, head);
        await AssignAsync(backoffice, mine, Roles.Player, cashier);
        await AssignAsync(backoffice, theirs, Roles.Player, otherCashier);
        // Cada uno usa la API (el front consulta /wallet/me todo el tiempo) y ahi el servicio aprende su nombre de usuario.
        foreach (var (id, name) in new[] { (mine, "ana"), (theirs, "beto"), (outsider, "carla") })
        {
            using var client = _app.NamedClientFor(id, name);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/wallet/me")).StatusCode);
        }

        using var cashierClient = _app.ClientFor(cashier, Roles.Player, Roles.Cashier);
        var members = await cashierClient.GetFromJsonAsync<JsonElement>("/cashier/members");
        var tree = await backoffice.GetFromJsonAsync<JsonElement>("/backoffice/wallet/hierarchy");

        var member = Assert.Single(members.EnumerateArray());
        Assert.Equal(("ana", mine), (member.GetProperty("displayName").GetString(), member.GetProperty("userId").GetGuid()));
        Assert.DoesNotContain("beto", members.GetRawText()); // el jugador de otro cajero
        Assert.DoesNotContain("carla", members.GetRawText());
        // Quien no esta en el arbol no deja su nombre guardado (no se acumulan nombres de quien no los necesita).
        Assert.DoesNotContain(tree.EnumerateArray(), n => n.GetProperty("userId").GetGuid() == outsider);
        // El backoffice, que ve todo el arbol, tambien los ve.
        Assert.Equal("beto", tree.EnumerateArray().Single(n => n.GetProperty("userId").GetGuid() == theirs).GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task A_loaded_player_keeps_showing_their_name_only_while_they_are_in_the_cashiers_jurisdiction()
    {
        var (head, cashier, otherCashier, player) = (await UserAsync(), await UserAsync(1_000), await UserAsync(), await UserAsync());
        using var backoffice = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);
        await AssignAsync(backoffice, head, Roles.HeadCashier, null);
        await AssignAsync(backoffice, cashier, Roles.Cashier, head);
        await AssignAsync(backoffice, otherCashier, Roles.Cashier, head);
        await AssignAsync(backoffice, player, Roles.Player, cashier);
        using (var playerClient = _app.NamedClientFor(player, "ana"))
        {
            await playerClient.GetAsync("/wallet/me");
        }

        using var cashierClient = _app.ClientFor(cashier, Roles.Player, Roles.Cashier);
        await LoadAsync(cashierClient, player, 50, "n-1");
        var before = await cashierClient.GetFromJsonAsync<JsonElement>("/cashier/transfers");
        await AssignAsync(backoffice, player, Roles.Player, otherCashier); // lo mueven a otro cajero
        var after = await cashierClient.GetFromJsonAsync<JsonElement>("/cashier/transfers");

        Assert.Equal("ana", Assert.Single(before.EnumerateArray()).GetProperty("targetName").GetString());
        Assert.Equal(JsonValueKind.Null, Assert.Single(after.EnumerateArray()).GetProperty("targetName").ValueKind);
    }

    [Fact]
    public async Task A_very_long_or_odd_user_name_is_cleaned_and_capped()
    {
        var (head, cashier, player) = (await UserAsync(), await UserAsync(), await UserAsync());
        using var backoffice = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);
        await AssignAsync(backoffice, head, Roles.HeadCashier, null);
        await AssignAsync(backoffice, cashier, Roles.Cashier, head);
        await AssignAsync(backoffice, player, Roles.Player, cashier);
        using (var playerClient = _app.NamedClientFor(player, "  " + new string('x', 100) + "  "))
        {
            await playerClient.GetAsync("/wallet/me");
        }

        using var cashierClient = _app.ClientFor(cashier, Roles.Player, Roles.Cashier);
        var members = await cashierClient.GetFromJsonAsync<JsonElement>("/cashier/members");

        Assert.Equal(new string('x', 60), Assert.Single(members.EnumerateArray()).GetProperty("displayName").GetString());
    }

    /// <summary>Un jefe de cajeros cualquiera, ya asignado (para los tests que solo necesitan un cajero valido).</summary>
    private async Task<Guid> HeadFor(HttpClient backoffice)
    {
        var head = await UserAsync();
        await AssignAsync(backoffice, head, Roles.HeadCashier, null);
        return head;
    }
}
