using Casino.BuildingBlocks;
using Casino.Modules.Wallet.Application;
using Casino.Modules.Wallet.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Casino.Modules.Wallet.Api;

public sealed record CreditRequest(long Amount);

public sealed record CommissionResponse(Guid ActorUserId, Guid TargetUserId, long LoadedAmount, long Amount, Guid TransactionId, DateTimeOffset OccurredAt);

public sealed record CommissionsResponse(IReadOnlyList<CommissionResponse> Items, long TotalPaid);

public sealed record WithdrawalRequestBody(long Amount);

/// <summary><paramref name="PlayerName"/> solo se informa a quien atiende el retiro (y al backoffice); al propio jugador no le hace falta.</summary>
public sealed record WithdrawalResponse(Guid Id, Guid PlayerUserId, string? PlayerName, long Amount, string Status, DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt);

public sealed record HierarchyAssignRequest(string? Level, Guid? ParentUserId);

public sealed record HierarchyNodeResponse(Guid UserId, string Level, string? DisplayName, Guid? ParentUserId, DateTimeOffset UpdatedAt);

public sealed record AuditEntryResponse(
    string Action, Guid ActorUserId, Guid TargetUserId, long Amount, Guid TransactionId, DateTimeOffset OccurredAt);

public sealed record CreditHistoryResponse(IReadOnlyList<AuditEntryResponse> Items, DateTimeOffset? NextBefore, long TotalAmount, int Count);

public sealed record OperationResponse(Guid TransactionId, bool IsDuplicate);

public sealed record AccountResponse(
    Guid AccountId,
    Guid UserId,
    long Available,
    long Reserved,
    long Version,
    IReadOnlyDictionary<Guid, long> OpenReservations);

// Reservar, liquidar y liberar NO son endpoints: son pasos internos de una apuesta que ocurren por mensajes.
// Un jugador solo puede consultar SU cuenta; acreditar fichas a mano es cosa del backoffice.
internal static class WalletEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";

    public static void Map(IEndpointRouteBuilder app)
    {
        CashierEndpoints.Map(app);

        var player = app.MapGroup("/wallet").WithTags("Wallet");
        player.RequireAuthorization(policy => policy.RequireRole(Roles.Player));
        player.AddEndpointFilter(MapDomainErrors);
        // El front consulta /wallet/me todo el tiempo: asi el nombre de quien esta en el arbol se mantiene al dia sin pedidos extra.
        player.AddEndpointFilter(CashierEndpoints.CaptureDisplayName);

        // La cuenta se deriva del usuario autenticado: no hay forma de pedir la de otro.
        player.MapGet("/me", (HttpContext http, WalletService wallet, CancellationToken ct) =>
            GetAccountAsync(http.User.GetUserId(), wallet, ct));

        player.MapGet("/me/balance", (HttpContext http, DateTimeOffset asOf, WalletService wallet, CancellationToken ct) =>
            GetBalanceAsync(http.User.GetUserId(), asOf, wallet, ct));

        // Extracto de movimientos: la cuenta sale del token, un jugador solo ve el suyo.
        player.MapGet("/me/movements", async (HttpContext http, WalletService wallet, int? limit, long? before, CancellationToken ct) =>
            Results.Ok(await wallet.GetMovementsAsync(PlayerIds.WalletAccountFor(http.User.GetUserId()), Math.Clamp(limit ?? 30, 1, 100), before, ct)));

        // Retiros: el jugador aparta fichas para que su cajero las cobre. La cuenta sale del token.
        player.MapPost("/withdrawals", async (HttpContext http, WithdrawalRequestBody body, WithdrawalService withdrawals, CancellationToken ct) =>
        {
            var key = http.Request.Headers[IdempotencyHeader].ToString();
            if (string.IsNullOrWhiteSpace(key))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: nameof(WalletError.InvalidIdempotencyKey),
                    detail: $"El header {IdempotencyHeader} es obligatorio.");
            }

            return Results.Ok(ToResponse(await withdrawals.RequestAsync(http.User.GetUserId(), body.Amount, key, ct)));
        });

        player.MapGet("/withdrawals", async (HttpContext http, WithdrawalService withdrawals, int? limit, CancellationToken ct) =>
            Results.Ok((await withdrawals.ListMineAsync(http.User.GetUserId(), Math.Clamp(limit ?? 20, 1, 100), ct)).Select(w => ToResponse(w))));

        player.MapPost("/withdrawals/{id:guid}/cancel", async (Guid id, HttpContext http, WithdrawalService withdrawals, CancellationToken ct) =>
            Results.Ok(ToResponse(await withdrawals.CancelAsync(http.User.GetUserId(), id, ct))));

        var backoffice = app.MapGroup("/backoffice/wallet").WithTags("Backoffice");
        backoffice.RequireAuthorization(policy => policy.RequireRole(Roles.Backoffice));
        backoffice.AddEndpointFilter(MapDomainErrors);

        backoffice.MapGet("/users/{userId:guid}", (Guid userId, WalletService wallet, CancellationToken ct) =>
            GetAccountAsync(userId, wallet, ct));

        backoffice.MapGet("/users/{userId:guid}/balance", (Guid userId, DateTimeOffset asOf, WalletService wallet, CancellationToken ct) =>
            GetBalanceAsync(userId, asOf, wallet, ct));

        // Ajuste manual de saldo; queda anotado en el registro de auditoria. La doble aprobacion llega en la fase 5.
        backoffice.MapPost("/users/{userId:guid}/credit", async (Guid userId, HttpContext context, CreditRequest body, WalletService wallet, BackofficeAudit audit, CancellationToken ct) =>
        {
            var key = context.Request.Headers[IdempotencyHeader].ToString();
            if (string.IsNullOrWhiteSpace(key))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: nameof(WalletError.InvalidIdempotencyKey),
                    detail: $"El header {IdempotencyHeader} es obligatorio.");
            }

            var accountId = PlayerIds.WalletAccountFor(userId);
            var outcome = await wallet.CreditAsync(accountId, key, body.Amount, ct);
            // Tambien si fue un duplicado: asi un reintento completa la anotacion si fallo la primera vez.
            await audit.RecordCreditAsync(context.User.GetUserId(), userId, accountId, body.Amount, key, outcome.TransactionId, ct);
            return Results.Ok(new OperationResponse(outcome.TransactionId, outcome.IsDuplicate));
        });

        // Historial general de cargas: filtros por jugador y fechas, paginado, con el total de fichas cargadas en el filtro.
        backoffice.MapGet("/credits", async (
            int? limit, DateTimeOffset? before, Guid? userId, DateTimeOffset? from, DateTimeOffset? to, BackofficeAudit audit, CancellationToken ct) =>
        {
            if (from is not null && to is not null && from >= to)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "InvalidRange", detail: "La fecha 'desde' debe ser anterior a 'hasta'.");
            }

            var history = await audit.QueryCreditsAsync(new CreditFilter(userId, from, to), Math.Clamp(limit ?? 25, 1, 100), before, ct);
            return Results.Ok(new CreditHistoryResponse(
                [.. history.Items.Select(e => new AuditEntryResponse(e.Action, e.ActorUserId, e.TargetUserId, e.Amount, e.TransactionId, e.OccurredAt))],
                history.NextBefore, history.TotalAmount, history.Count));
        });

        // Jerarquia de cargas: quien es jefe de cajeros, cajero o jugador y de quien depende. Los roles de Keycloak dan acceso; el arbol dice a quien se le puede cargar.
        backoffice.MapGet("/hierarchy", async (CashierService cashiers, CancellationToken ct) =>
            Results.Ok((await cashiers.ListAllAsync(ct)).Select(n => new HierarchyNodeResponse(n.Id, CashierService.NameOf(n.Level), n.DisplayName, n.ParentUserId, n.UpdatedAt))));

        backoffice.MapPut("/hierarchy/{userId:guid}", async (Guid userId, HierarchyAssignRequest body, HttpContext context, CashierService cashiers, CancellationToken ct) =>
        {
            if (!CashierService.TryParseLevel(body.Level, out var level))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: nameof(WalletError.InvalidHierarchy),
                    detail: $"El nivel tiene que ser '{Roles.Player}', '{Roles.Cashier}' o '{Roles.HeadCashier}'.");
            }

            var node = await cashiers.AssignAsync(context.User.GetUserId(), userId, level, body.ParentUserId, ct);
            return Results.Ok(new HierarchyNodeResponse(node.Id, CashierService.NameOf(node.Level), node.DisplayName, node.ParentUserId, node.UpdatedAt));
        });

        // Retiros sin cajero (jugador sin cajero o jefe de cajeros): los atiende el backoffice; las fichas vuelven a la casa.
        backoffice.MapGet("/withdrawals", async (WithdrawalService withdrawals, CancellationToken ct) =>
            Results.Ok((await withdrawals.ListUnassignedAsync(ct)).Select(v => ToResponse(v.Request, v.PlayerName))));

        backoffice.MapPost("/withdrawals/{id:guid}/pay", async (Guid id, HttpContext http, WithdrawalService withdrawals, CancellationToken ct) =>
            Results.Ok(ToResponse(await withdrawals.PayToHouseAsync(http.User.GetUserId(), id, ct))));

        backoffice.MapPost("/withdrawals/{id:guid}/reject", async (Guid id, HttpContext http, WithdrawalService withdrawals, CancellationToken ct) =>
            Results.Ok(ToResponse(await withdrawals.RejectUnassignedAsync(http.User.GetUserId(), id, ct))));

        // Comisiones que pago la casa (fichas emitidas por incentivo), con el total.
        backoffice.MapGet("/commissions", async (int? limit, CashierService cashiers, CancellationToken ct) =>
        {
            var (items, total) = await cashiers.ListCommissionsAsync(Math.Clamp(limit ?? 50, 1, 200), ct);
            return Results.Ok(new CommissionsResponse(
                [.. items.Select(c => new CommissionResponse(c.ActorUserId, c.TargetUserId, c.LoadedAmount, c.Amount, c.TransferTransactionId, c.OccurredAt))], total));
        });

        // Registro de auditoria: quien cargo fichas, a quien y cuando.
        backoffice.MapGet("/audit", async (int? limit, BackofficeAudit audit, CancellationToken ct) =>
        {
            var entries = await audit.ListAsync(Math.Clamp(limit ?? 50, 1, 200), ct);
            return Results.Ok(entries.Select(e => new AuditEntryResponse(e.Action, e.ActorUserId, e.TargetUserId, e.Amount, e.TransactionId, e.OccurredAt)));
        });
    }

    private static async Task<IResult> GetAccountAsync(Guid userId, WalletService wallet, CancellationToken ct)
    {
        var account = await wallet.GetAsync(PlayerIds.WalletAccountFor(userId), ct);
        return Results.Ok(new AccountResponse(
            account.Id, account.UserId, account.Available, account.Reserved, account.Version, account.OpenReservations));
    }

    private static async Task<IResult> GetBalanceAsync(Guid userId, DateTimeOffset asOf, WalletService wallet, CancellationToken ct)
    {
        var balance = await wallet.GetBalanceAtAsync(PlayerIds.WalletAccountFor(userId), asOf, ct);
        return balance is null
            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "AccountNotOpenYet", detail: "La cuenta aun no existia en esa fecha.")
            : Results.Ok(balance);
    }

    internal static WithdrawalResponse ToResponse(WithdrawalRequest w, string? playerName = null) =>
        new(w.Id, w.PlayerUserId, playerName, w.Amount, w.Status.ToString(), w.CreatedAt, w.ResolvedAt);

    internal static async ValueTask<object?> MapDomainErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (WalletDomainException ex)
        {
            return Results.Problem(statusCode: StatusFor(ex.Error), title: ex.Error.ToString(), detail: ex.Message);
        }
    }

    internal static int StatusFor(WalletError error) => error switch
    {
        WalletError.AccountNotFound or WalletError.ReservationNotFound or WalletError.TransactionNotFound
            => StatusCodes.Status404NotFound,
        WalletError.InvalidAmount or WalletError.InvalidIdempotencyKey or WalletError.InvalidTransfer
            => StatusCodes.Status400BadRequest,
        WalletError.NotInJurisdiction
            => StatusCodes.Status403Forbidden,
        WalletError.WithdrawalNotFound
            => StatusCodes.Status404NotFound,
        WalletError.InsufficientFunds
            => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status409Conflict,
    };
}
