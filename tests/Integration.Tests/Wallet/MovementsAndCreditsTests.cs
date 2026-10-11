using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casino.BuildingBlocks;
using Casino.Modules.Wallet.Application;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Casino.Integration.Tests.Wallet;

/// <summary>
/// El extracto de movimientos del jugador (derivado del ledger) y el historial general de cargas del administrador
/// (filtros, paginacion y totales).
/// </summary>
[Collection(WalletDbDefinition.Name)]
public sealed class MovementsAndCreditsTests : IDisposable
{
    private readonly CasinoCluster _app;

    public MovementsAndCreditsTests(PostgresFixture db)
    {
        _app = TestAuth.StartApp(db);
    }

    public void Dispose() => _app.Dispose();

    private WalletService Wallet => _app.Services.GetRequiredService<WalletService>();

    private async Task<(Guid UserId, Guid AccountId)> PlayerAsync()
    {
        var userId = Guid.NewGuid();
        return (userId, await Wallet.OpenAccountAsync(userId));
    }

    private static Task<HttpResponseMessage> CreditAsync(HttpClient client, Guid userId, long amount, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/backoffice/wallet/users/{userId}/credit") { Content = JsonContent.Create(new { amount }) };
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    // ---- Movimientos del jugador ----

    [Fact]
    public async Task The_statement_lists_every_change_of_the_available_chips_with_the_resulting_balance()
    {
        var (userId, accountId) = await PlayerAsync();
        var bet1 = Guid.NewGuid();
        var bet2 = Guid.NewGuid();
        var bet3 = Guid.NewGuid();
        await Wallet.CreditAsync(accountId, "welcome-bonus", 1_000);
        await Wallet.CreditAsync(accountId, "admin-1", 500);
        await Wallet.ReserveAsync(accountId, "r1", bet1, 100);
        await Wallet.SettleAsync(accountId, "s1", bet1, 250); // gana
        await Wallet.ReserveAsync(accountId, "r2", bet2, 200);
        await Wallet.SettleAsync(accountId, "s2", bet2, 0); // pierde: no mueve fichas disponibles
        await Wallet.ReserveAsync(accountId, "r3", bet3, 50);
        await Wallet.ReleaseAsync(accountId, "x3", bet3); // se anula: vuelven

        var page = await Wallet.GetMovementsAsync(accountId, 50, before: null);

        // De la mas reciente a la mas vieja.
        Assert.Equal(
            [
                (MovementKind.Refund, 50L, 1_450L),
                (MovementKind.Stake, -50L, 1_400L),
                (MovementKind.Stake, -200L, 1_450L),
                (MovementKind.Prize, 250L, 1_650L),
                (MovementKind.Stake, -100L, 1_400L),
                (MovementKind.Credit, 500L, 1_500L),
                (MovementKind.WelcomeBonus, 1_000L, 1_000L),
            ],
            page.Items.Select(m => (m.Kind, m.Delta, m.BalanceAfter)));
        Assert.Null(page.NextBefore);

        // El ultimo saldo del extracto es exactamente el disponible de la cuenta: se deriva del ledger, no se guarda aparte.
        Assert.Equal((await Wallet.GetAsync(accountId)).Available, page.Items[0].BalanceAfter);
        Assert.Equal(bet1, page.Items.Single(m => m.Kind == MovementKind.Prize).Reference);
        Assert.Equal(userId, (await Wallet.GetAsync(accountId)).UserId);
    }

    [Fact]
    public async Task A_stake_its_prize_and_its_refund_carry_the_game_where_it_was_bet()
    {
        var (_, accountId) = await PlayerAsync();
        var won = Guid.NewGuid();
        var cancelled = Guid.NewGuid();
        await Wallet.CreditAsync(accountId, "welcome-bonus", 1_000);
        await Wallet.ReserveAsync(accountId, "r1", won, 100, gameId: "roulette");
        await Wallet.SettleAsync(accountId, "s1", won, 250);
        await Wallet.ReserveAsync(accountId, "r2", cancelled, 50, gameId: "slots");
        await Wallet.ReleaseAsync(accountId, "x2", cancelled);
        await Wallet.ReserveAsync(accountId, "r3", Guid.NewGuid(), 10); // sin juego (apuesta anterior a que se guardara)

        var page = await Wallet.GetMovementsAsync(accountId, 50, before: null);

        Assert.Equal(
            [(MovementKind.Stake, (string?)null), (MovementKind.Refund, "slots"), (MovementKind.Stake, "slots"), (MovementKind.Prize, "roulette"), (MovementKind.Stake, "roulette"), (MovementKind.WelcomeBonus, null)],
            page.Items.Select(m => (m.Kind, m.GameId)));
    }

    [Fact]
    public async Task The_statement_is_paged_with_a_cursor_that_never_repeats_or_skips()
    {
        var (_, accountId) = await PlayerAsync();
        for (var i = 1; i <= 7; i++)
        {
            await Wallet.CreditAsync(accountId, $"p-{i}", i * 10);
        }

        var first = await Wallet.GetMovementsAsync(accountId, 3, before: null);
        var second = await Wallet.GetMovementsAsync(accountId, 3, first.NextBefore);
        var third = await Wallet.GetMovementsAsync(accountId, 3, second.NextBefore);

        Assert.Equal([70L, 60L, 50L], first.Items.Select(m => m.Delta));
        Assert.Equal([40L, 30L, 20L], second.Items.Select(m => m.Delta));
        Assert.Equal([10L], third.Items.Select(m => m.Delta));
        Assert.NotNull(first.NextBefore);
        Assert.NotNull(second.NextBefore);
        Assert.Null(third.NextBefore);
    }

    [Fact]
    public async Task A_reversal_shows_up_as_a_movement_that_undoes_the_original()
    {
        var (_, accountId) = await PlayerAsync();
        var credit = await Wallet.CreditAsync(accountId, "to-undo", 300);
        await Wallet.ReverseAsync(accountId, "undo", credit.TransactionId);

        var page = await Wallet.GetMovementsAsync(accountId, 10, before: null);

        Assert.Equal([(MovementKind.Reversal, -300L, 0L), (MovementKind.Credit, 300L, 300L)], page.Items.Select(m => (m.Kind, m.Delta, m.BalanceAfter)));
        Assert.Equal(credit.TransactionId, page.Items[0].Reference);
    }

    [Fact]
    public async Task An_account_with_no_movements_has_an_empty_statement_and_an_unknown_one_is_not_found()
    {
        var (_, accountId) = await PlayerAsync();

        Assert.Empty((await Wallet.GetMovementsAsync(accountId, 10, before: null)).Items);
        await Assert.ThrowsAsync<Casino.Modules.Wallet.Domain.WalletDomainException>(() => Wallet.GetMovementsAsync(Guid.NewGuid(), 10, before: null));
    }

    [Fact]
    public async Task A_player_only_sees_their_own_statement_and_must_be_logged_in()
    {
        var (userId, accountId) = await PlayerAsync();
        var (otherId, otherAccount) = await PlayerAsync();
        await Wallet.CreditAsync(accountId, "mine", 111);
        await Wallet.CreditAsync(otherAccount, "theirs", 222);
        using var me = _app.ClientFor(userId);
        using var other = _app.ClientFor(otherId);

        var mine = await me.GetFromJsonAsync<JsonElement>("/wallet/me/movements");
        var theirs = await other.GetFromJsonAsync<JsonElement>("/wallet/me/movements");

        Assert.Equal([111L], mine.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("delta").GetInt64()));
        Assert.Equal([222L], theirs.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("delta").GetInt64()));
        using var anonymous = _app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/wallet/me/movements")).StatusCode);
    }

    [Fact]
    public async Task The_statement_limit_is_bounded()
    {
        var (userId, accountId) = await PlayerAsync();
        await Wallet.CreditAsync(accountId, "one", 5);
        using var me = _app.ClientFor(userId);

        Assert.Equal(HttpStatusCode.OK, (await me.GetAsync("/wallet/me/movements?limit=100000")).StatusCode);
        var tiny = await me.GetFromJsonAsync<JsonElement>("/wallet/me/movements?limit=0"); // se ajusta al minimo
        Assert.Single(tiny.GetProperty("items").EnumerateArray());
    }

    // ---- Historial de cargas del administrador ----

    [Fact]
    public async Task The_credit_history_filters_by_player_and_adds_up_only_what_matches()
    {
        var (alice, _) = await PlayerAsync();
        var (bob, _) = await PlayerAsync();
        using var admin = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);
        await CreditAsync(admin, alice, 100, "f-a1");
        await CreditAsync(admin, alice, 250, "f-a2");
        await CreditAsync(admin, bob, 40, "f-b1");

        var history = await admin.GetFromJsonAsync<JsonElement>($"/backoffice/wallet/credits?userId={alice}");

        Assert.Equal(2, history.GetProperty("count").GetInt32());
        Assert.Equal(350, history.GetProperty("totalAmount").GetInt64());
        Assert.All(history.GetProperty("items").EnumerateArray(), e => Assert.Equal(alice, e.GetProperty("targetUserId").GetGuid()));
        Assert.Equal([250L, 100L], history.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("amount").GetInt64()));
    }

    [Fact]
    public async Task The_credit_history_is_paged_and_the_totals_cover_every_page()
    {
        var (carol, _) = await PlayerAsync();
        using var admin = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);
        for (var i = 1; i <= 5; i++)
        {
            await CreditAsync(admin, carol, i * 10, $"pg-{i}");
            await Task.Delay(15);
        }

        var first = await admin.GetFromJsonAsync<JsonElement>($"/backoffice/wallet/credits?userId={carol}&limit=2");
        var cursor = first.GetProperty("nextBefore").GetDateTimeOffset();
        var second = await admin.GetFromJsonAsync<JsonElement>($"/backoffice/wallet/credits?userId={carol}&limit=2&before={Uri.EscapeDataString(cursor.ToString("o"))}");

        Assert.Equal([50L, 40L], first.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("amount").GetInt64()));
        Assert.Equal([30L, 20L], second.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("amount").GetInt64()));
        Assert.Equal(5, second.GetProperty("count").GetInt32()); // el total no depende de la pagina
        Assert.Equal(150, second.GetProperty("totalAmount").GetInt64());
    }

    [Fact]
    public async Task The_credit_history_filters_by_date_range()
    {
        var (dave, _) = await PlayerAsync();
        using var admin = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);
        await CreditAsync(admin, dave, 10, "d-1");
        var middle = DateTimeOffset.UtcNow;
        await Task.Delay(50);
        await CreditAsync(admin, dave, 20, "d-2");
        var iso = Uri.EscapeDataString(middle.ToString("o"));

        var after = await admin.GetFromJsonAsync<JsonElement>($"/backoffice/wallet/credits?userId={dave}&from={iso}");
        var before = await admin.GetFromJsonAsync<JsonElement>($"/backoffice/wallet/credits?userId={dave}&to={iso}");

        Assert.Equal(20, after.GetProperty("totalAmount").GetInt64());
        Assert.Equal(10, before.GetProperty("totalAmount").GetInt64());
    }

    [Fact]
    public async Task A_backwards_date_range_is_rejected_and_a_filter_with_no_match_is_empty()
    {
        using var admin = _app.ClientFor(Guid.NewGuid(), Roles.Backoffice);

        var backwards = await admin.GetAsync("/backoffice/wallet/credits?from=2026-10-09T00:00:00Z&to=2026-10-01T00:00:00Z");
        var nobody = await admin.GetFromJsonAsync<JsonElement>($"/backoffice/wallet/credits?userId={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);
        Assert.Equal(0, nobody.GetProperty("count").GetInt32());
        Assert.Equal(0, nobody.GetProperty("totalAmount").GetInt64());
        Assert.Empty(nobody.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, nobody.GetProperty("nextBefore").ValueKind);
    }

    [Fact]
    public async Task Only_backoffice_can_read_the_credit_history()
    {
        using var player = _app.ClientFor(Guid.NewGuid());
        using var anonymous = _app.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await player.GetAsync("/backoffice/wallet/credits")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/backoffice/wallet/credits")).StatusCode);
    }
}
