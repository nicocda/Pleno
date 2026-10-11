using Casino.BuildingBlocks;
using Casino.Modules.Wallet.Application;
using Casino.Modules.Wallet.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Casino.Modules.Wallet.Api;

public sealed record LoadChipsRequest(Guid ToUserId, long Amount);

/// <summary><paramref name="CommissionPermille"/>: lo que cobra por cada carga (milesimas; 20 = 2 %).</summary>
public sealed record CashierMeResponse(Guid UserId, string Level, Guid? ParentUserId, int CommissionPermille);

/// <summary><paramref name="Commission"/>: lo que la casa le pago a quien cargo por esta carga (0 si no cobra o llego al tope).</summary>
public sealed record LoadChipsResponse(Guid TransactionId, bool IsDuplicate, long Commission);

public sealed record JurisdictionMemberResponse(Guid UserId, string Level, string? DisplayName, long? Available, long? Reserved);

/// <summary><paramref name="TargetName"/> solo viene si el destinatario sigue siendo de mi jurisdiccion (no se filtran nombres de quien ya no esta a mi cargo).</summary>
public sealed record CashierTransferResponse(Guid TargetUserId, string? TargetName, long Amount, Guid TransactionId, DateTimeOffset OccurredAt);

/// <summary>
/// Cajeros y jefes de cajeros. La identidad y el nivel salen del token (nunca del cuerpo): el jefe carga a sus cajeros y el cajero a sus jugadores, siempre desde
/// su propio saldo. Ver ADR 0014.
/// </summary>
internal static class CashierEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/cashier").WithTags("Cashier");
        group.RequireAuthorization(policy => policy.RequireRole(Roles.Cashier, Roles.HeadCashier));
        group.AddEndpointFilter(WalletEndpoints.MapDomainErrors);
        group.AddEndpointFilter(CaptureDisplayName);

        // Mi lugar en la jerarquia (null si el backoffice todavia no me asigno uno).
        group.MapGet("/me", async (HttpContext http, CashierService cashiers, CancellationToken ct) =>
        {
            var userId = http.User.GetUserId();
            var node = await cashiers.GetNodeAsync(userId, ct);
            return node is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: nameof(WalletError.NotInJurisdiction), detail: "Todavia no te asignaron un lugar en la jerarquia de cajeros.")
                : Results.Ok(new CashierMeResponse(userId, CashierService.NameOf(node.Level), node.ParentUserId, cashiers.CommissionPermilleFor(node.Level)));
        });

        // La gente de mi jurisdiccion directa, con su saldo.
        group.MapGet("/members", async (HttpContext http, CashierService cashiers, int? limit, CancellationToken ct) =>
        {
            var members = await cashiers.ListMembersAsync(http.User.GetUserId(), Math.Clamp(limit ?? 100, 1, 500), ct);
            return Results.Ok(members.Select(m => new JurisdictionMemberResponse(m.UserId, CashierService.NameOf(m.Level), m.DisplayName, m.Available, m.Reserved)));
        });

        // Cargar fichas desde mi saldo a alguien de mi jurisdiccion.
        group.MapPost("/transfers", async (HttpContext http, LoadChipsRequest body, CashierService cashiers, CancellationToken ct) =>
        {
            var key = http.Request.Headers[IdempotencyHeader].ToString();
            if (string.IsNullOrWhiteSpace(key))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: nameof(WalletError.InvalidIdempotencyKey),
                    detail: $"El header {IdempotencyHeader} es obligatorio.");
            }

            var roles = http.User.FindAll(System.Security.Claims.ClaimTypes.Role).Select(c => c.Value);
            var result = await cashiers.LoadChipsAsync(http.User.GetUserId(), roles, body.ToUserId, body.Amount, key, ct);
            return Results.Ok(new LoadChipsResponse(result.Outcome.TransactionId, result.Outcome.IsDuplicate, result.Commission));
        });

        // Retiros pendientes de la gente a mi cargo: los cobro (las fichas pasan a mi cuenta) o los rechazo (vuelven al jugador).
        group.MapGet("/withdrawals", async (HttpContext http, WithdrawalService withdrawals, CancellationToken ct) =>
            Results.Ok((await withdrawals.ListPendingForAsync(http.User.GetUserId(), ct)).Select(v => WalletEndpoints.ToResponse(v.Request, v.PlayerName))));

        group.MapPost("/withdrawals/{id:guid}/pay", async (Guid id, HttpContext http, WithdrawalService withdrawals, CancellationToken ct) =>
            Results.Ok(WalletEndpoints.ToResponse((await withdrawals.PayAsync(http.User.GetUserId(), id, ct)))));

        group.MapPost("/withdrawals/{id:guid}/reject", async (Guid id, HttpContext http, WithdrawalService withdrawals, CancellationToken ct) =>
            Results.Ok(WalletEndpoints.ToResponse((await withdrawals.RejectAsync(http.User.GetUserId(), id, ct)))));

        // Mis ultimas cargas.
        group.MapGet("/transfers", async (HttpContext http, BackofficeAudit audit, CashierService cashiers, int? limit, CancellationToken ct) =>
        {
            var me = http.User.GetUserId();
            var entries = await audit.ListTransfersByActorAsync(me, Math.Clamp(limit ?? 25, 1, 100), ct);
            // Solo se muestran los nombres de quienes siguen a mi cargo.
            var names = await cashiers.GetMemberNamesAsync(me, ct);
            return Results.Ok(entries.Select(e => new CashierTransferResponse(e.TargetUserId, names.GetValueOrDefault(e.TargetUserId), e.Amount, e.TransactionId, e.OccurredAt)));
        });
    }

    /// <summary>Guarda el nombre de usuario del token de quien esta en el arbol (ver <see cref="HierarchyNode.DisplayName"/>). Se ejecuta antes de cada pedido autenticado; nunca lo rechaza.</summary>
    internal static async ValueTask<object?> CaptureDisplayName(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.User.Identity?.IsAuthenticated == true && http.User.TryGetUserId(out var userId))
        {
            await http.RequestServices.GetRequiredService<CashierService>().TouchDisplayNameAsync(userId, http.User.FindFirst("preferred_username")?.Value, http.RequestAborted);
        }

        return await next(context);
    }
}
