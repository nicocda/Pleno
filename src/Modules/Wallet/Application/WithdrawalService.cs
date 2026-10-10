using System.Security.Cryptography;
using System.Text;
using Casino.BuildingBlocks;
using Casino.Modules.Wallet.Domain;
using JasperFx;
using Marten;
using Marten.Exceptions;

namespace Casino.Modules.Wallet.Application;

public enum WithdrawalStatus
{
    /// <summary>Las fichas estan en reserva esperando que su cajero las cobre.</summary>
    Pending = 0,

    /// <summary>El cajero (o la casa) las cobro: las fichas ya no estan en la cuenta del jugador.</summary>
    Paid = 1,

    /// <summary>El cajero rechazo el pedido: las fichas volvieron al jugador.</summary>
    Rejected = 2,

    /// <summary>El jugador cancelo el pedido: las fichas volvieron.</summary>
    Cancelled = 3,

    /// <summary>Nadie lo atendio a tiempo: la Wallet libero la reserva y las fichas volvieron al jugador.</summary>
    Expired = 4,
}

/// <summary>
/// El pedido de retiro de un jugador. Sus fichas quedan en RESERVA (no se pueden jugar) hasta que su cajero las cobre, las rechace, el jugador cancele o venza el plazo. El id del pedido
/// es tambien el de la reserva en la Wallet.
/// </summary>
public sealed class WithdrawalRequest
{
    public required Guid Id { get; init; }

    public required Guid PlayerUserId { get; init; }

    /// <summary>Quien lo atiende: el padre del jugador en el arbol al pedir (null = lo atiende el backoffice: jugador sin cajero o jefe de cajeros).</summary>
    public Guid? ApproverUserId { get; init; }

    public required long Amount { get; init; }

    public WithdrawalStatus Status { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>Quien lo resolvio (cajero, backoffice o el propio jugador al cancelar).</summary>
    public Guid? ResolvedBy { get; set; }
}

/// <summary>Un pedido con el nombre de usuario del jugador (solo para quien lo atiende y el backoffice).</summary>
public sealed record WithdrawalView(WithdrawalRequest Request, string? PlayerName);

/// <summary>
/// El camino de salida de las fichas, el inverso de la carga: el jugador pide retirar y sus fichas pasan a una reserva (mismo protocolo reservar → resolver de los juegos); su cajero las
/// cobra (las fichas pasan a su cuenta en una sola transaccion), las rechaza, el jugador cancela o la reserva vence y vuelven solas. Un jugador sin cajero (o un jefe) lo atiende el
/// backoffice, que las devuelve a la casa. Ver ADR 0014.
/// </summary>
public sealed class WithdrawalService(IDocumentStore store, WalletService wallet, TimeProvider clock, CashierOptions options)
{
    public const string GameId = "withdrawal";

    private static string ReserveKey(Guid id) => $"withdraw:{id:N}";

    /// <summary>El id del pedido sale de (jugador, clave): repetir el pedido con la misma clave es el mismo pedido.</summary>
    internal static Guid IdFor(Guid playerUserId, string idempotencyKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"withdrawal:{playerUserId:N}:{idempotencyKey}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    // ---- Jugador ----

    /// <summary>Pide retirar: aparta las fichas en una reserva. Idempotente por clave; con la misma clave y otro monto se rechaza.</summary>
    public async Task<WithdrawalRequest> RequestAsync(Guid playerUserId, long amount, string idempotencyKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
        {
            throw new WalletDomainException(WalletError.InvalidIdempotencyKey, "La IdempotencyKey es obligatoria y de hasta 128 caracteres.");
        }

        if (amount < options.MinWithdrawal)
        {
            throw new WalletDomainException(WalletError.InvalidAmount, $"El retiro minimo es de {options.MinWithdrawal} fichas.");
        }

        var id = IdFor(playerUserId, idempotencyKey);
        await using (var read = store.QuerySession())
        {
            var existing = await read.LoadAsync<WithdrawalRequest>(id, ct);
            if (existing is not null)
            {
                return existing.Amount == amount
                    ? await RefreshAsync(existing, ct)
                    : throw new WalletDomainException(WalletError.IdempotencyKeyReused, "La IdempotencyKey ya se uso con otro monto.");
            }
        }

        await using var nodes = store.QuerySession();
        var approver = (await nodes.LoadAsync<HierarchyNode>(playerUserId, ct))?.ParentUserId;

        // Primero la reserva (falla si no alcanzan las fichas y no deja nada); despues el pedido. Si el proceso cae entre las dos, repetir con la misma clave completa lo que falta.
        await wallet.ReserveAsync(PlayerIds.WalletAccountFor(playerUserId), ReserveKey(id), id, amount, TimeSpan.FromHours(options.WithdrawalExpiryHours), GameId, ct);

        var request = new WithdrawalRequest
        {
            Id = id,
            PlayerUserId = playerUserId,
            ApproverUserId = approver,
            Amount = amount,
            Status = WithdrawalStatus.Pending,
            CreatedAt = clock.GetUtcNow(),
        };
        try
        {
            await using var session = store.LightweightSession();
            session.Insert(request);
            await session.SaveChangesAsync(ct);
            return request;
        }
        catch (Exception ex) when (ex is DocumentAlreadyExistsException || ex.InnerException is DocumentAlreadyExistsException)
        {
            await using var again = store.QuerySession();
            return await again.LoadAsync<WithdrawalRequest>(id, ct) ?? request; // dos pedidos iguales a la vez: vale el primero
        }
    }

    public async Task<IReadOnlyList<WithdrawalRequest>> ListMineAsync(Guid playerUserId, int limit, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        var mine = await session.Query<WithdrawalRequest>().Where(w => w.PlayerUserId == playerUserId).OrderByDescending(w => w.CreatedAt).Take(limit).ToListAsync(ct);
        return await RefreshAllAsync(mine, ct);
    }

    /// <summary>El jugador cancela su pedido pendiente: las fichas vuelven.</summary>
    public async Task<WithdrawalRequest> CancelAsync(Guid playerUserId, Guid id, CancellationToken ct = default)
    {
        var request = await GetOwnedAsync(playerUserId, id, ct);
        return await ReleaseAsync(request, playerUserId, WithdrawalStatus.Cancelled, $"withdraw-cancel:{id:N}", ct);
    }

    // ---- Cajero o jefe (quien lo atiende) ----

    /// <summary>Los pedidos pendientes de la gente a mi cargo (con su nombre de usuario).</summary>
    public async Task<IReadOnlyList<WithdrawalView>> ListPendingForAsync(Guid approverUserId, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        var pending = await session.Query<WithdrawalRequest>().Where(w => w.ApproverUserId == approverUserId && w.Status == WithdrawalStatus.Pending).OrderBy(w => w.CreatedAt).Take(200).ToListAsync(ct);
        return await WithNamesAsync(await RefreshAllAsync(pending, ct), ct);
    }

    /// <summary>Cobra el retiro: las fichas pasan de la reserva del jugador a la cuenta de quien lo atiende, en una sola transaccion.</summary>
    public async Task<WithdrawalRequest> PayAsync(Guid approverUserId, Guid id, CancellationToken ct = default)
    {
        var request = await GetAsync(id, ct);
        if (request.ApproverUserId != approverUserId)
        {
            throw new WalletDomainException(WalletError.NotInJurisdiction, "Ese retiro no es de tu jurisdiccion.");
        }

        return await PayCoreAsync(request, approverUserId, toHouse: false, ct);
    }

    public async Task<WithdrawalRequest> RejectAsync(Guid approverUserId, Guid id, CancellationToken ct = default)
    {
        var request = await GetAsync(id, ct);
        if (request.ApproverUserId != approverUserId)
        {
            throw new WalletDomainException(WalletError.NotInJurisdiction, "Ese retiro no es de tu jurisdiccion.");
        }

        return await ReleaseAsync(request, approverUserId, WithdrawalStatus.Rejected, $"withdraw-reject:{id:N}", ct);
    }

    // ---- Backoffice: los que no tienen cajero ----

    public async Task<IReadOnlyList<WithdrawalView>> ListUnassignedAsync(CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        var pending = await session.Query<WithdrawalRequest>().Where(w => w.ApproverUserId == null && w.Status == WithdrawalStatus.Pending).OrderBy(w => w.CreatedAt).Take(200).ToListAsync(ct);
        return await WithNamesAsync(await RefreshAllAsync(pending, ct), ct);
    }

    /// <summary>Cobra un retiro sin cajero: las fichas vuelven a la casa (liquidacion de la reserva sin premio).</summary>
    public async Task<WithdrawalRequest> PayToHouseAsync(Guid backofficeUserId, Guid id, CancellationToken ct = default)
    {
        var request = await GetAsync(id, ct);
        if (request.ApproverUserId is not null)
        {
            throw new WalletDomainException(WalletError.NotInJurisdiction, "Ese retiro lo atiende su cajero.");
        }

        return await PayCoreAsync(request, backofficeUserId, toHouse: true, ct);
    }

    public async Task<WithdrawalRequest> RejectUnassignedAsync(Guid backofficeUserId, Guid id, CancellationToken ct = default)
    {
        var request = await GetAsync(id, ct);
        if (request.ApproverUserId is not null)
        {
            throw new WalletDomainException(WalletError.NotInJurisdiction, "Ese retiro lo atiende su cajero.");
        }

        return await ReleaseAsync(request, backofficeUserId, WithdrawalStatus.Rejected, $"withdraw-reject:{id:N}", ct);
    }

    // ---- Interno ----

    private async Task<WithdrawalRequest> PayCoreAsync(WithdrawalRequest request, Guid actor, bool toHouse, CancellationToken ct)
    {
        if (request.Status == WithdrawalStatus.Paid && request.ResolvedBy == actor)
        {
            return request; // reintento de lo que ya se hizo
        }

        request = await RefreshAsync(request, ct);
        if (request.Status != WithdrawalStatus.Pending)
        {
            throw new WalletDomainException(WalletError.WithdrawalNotPending, $"Ese retiro ya no esta pendiente ({request.Status}).");
        }

        var player = PlayerIds.WalletAccountFor(request.PlayerUserId);
        try
        {
            if (toHouse)
            {
                await wallet.SettleAsync(player, $"payout-house:{request.Id:N}", request.Id, 0, ct);
            }
            else
            {
                await wallet.PayOutReservationAsync(request.PlayerUserId, actor, request.Id, $"payout:{request.Id:N}", ct);
            }
        }
        catch (WalletDomainException ex) when (ex.Error is WalletError.ReservationNotOpen or WalletError.ReservationNotFound)
        {
            // En el mismo instante el jugador cancelo o la reserva vencio: gano esa decision.
            request = await RefreshAsync(request, ct);
            throw new WalletDomainException(WalletError.WithdrawalNotPending, $"Ese retiro ya no esta pendiente ({request.Status}).");
        }

        return await CloseAsync(request, WithdrawalStatus.Paid, actor, ct);
    }

    private async Task<WithdrawalRequest> ReleaseAsync(WithdrawalRequest request, Guid actor, WithdrawalStatus outcome, string key, CancellationToken ct)
    {
        if (request.Status == outcome && request.ResolvedBy == actor)
        {
            return request; // reintento de lo que ya se hizo
        }

        request = await RefreshAsync(request, ct);
        if (request.Status != WithdrawalStatus.Pending)
        {
            throw new WalletDomainException(WalletError.WithdrawalNotPending, $"Ese retiro ya no esta pendiente ({request.Status}).");
        }

        try
        {
            await wallet.ReleaseAsync(PlayerIds.WalletAccountFor(request.PlayerUserId), key, request.Id, ct);
        }
        catch (WalletDomainException ex) when (ex.Error is WalletError.ReservationNotOpen or WalletError.ReservationNotFound)
        {
            request = await RefreshAsync(request, ct);
            throw new WalletDomainException(WalletError.WithdrawalNotPending, $"Ese retiro ya no esta pendiente ({request.Status}).");
        }

        return await CloseAsync(request, outcome, actor, ct);
    }

    private async Task<WithdrawalRequest> CloseAsync(WithdrawalRequest request, WithdrawalStatus status, Guid? actor, CancellationToken ct)
    {
        await using var session = store.LightweightSession();
        var fresh = await session.LoadAsync<WithdrawalRequest>(request.Id, ct) ?? request;
        fresh.Status = status;
        fresh.ResolvedAt = clock.GetUtcNow();
        fresh.ResolvedBy = actor;
        session.Store(fresh);
        await session.SaveChangesAsync(ct);
        return fresh;
    }

    /// <summary>
    /// Un pedido "pendiente" cuya reserva ya no esta abierta se vencio (la Wallet la libero sola): se marca como vencido. Asi no hace falta un mensaje de vuelta de la Wallet y el estado
    /// nunca miente.
    /// </summary>
    private async Task<WithdrawalRequest> RefreshAsync(WithdrawalRequest request, CancellationToken ct)
    {
        if (request.Status != WithdrawalStatus.Pending)
        {
            return request;
        }

        var account = await wallet.GetAsync(PlayerIds.WalletAccountFor(request.PlayerUserId), ct);
        return account.OpenReservations.ContainsKey(request.Id) ? request : await CloseAsync(request, WithdrawalStatus.Expired, null, ct);
    }

    private async Task<IReadOnlyList<WithdrawalRequest>> RefreshAllAsync(IReadOnlyList<WithdrawalRequest> requests, CancellationToken ct)
    {
        var result = new List<WithdrawalRequest>(requests.Count);
        foreach (var request in requests)
        {
            result.Add(await RefreshAsync(request, ct));
        }

        return result;
    }

    private async Task<IReadOnlyList<WithdrawalView>> WithNamesAsync(IReadOnlyList<WithdrawalRequest> requests, CancellationToken ct)
    {
        await using var session = store.QuerySession();
        var views = new List<WithdrawalView>(requests.Count);
        foreach (var request in requests.Where(r => r.Status == WithdrawalStatus.Pending))
        {
            var node = await session.LoadAsync<HierarchyNode>(request.PlayerUserId, ct);
            views.Add(new WithdrawalView(request, node?.DisplayName));
        }

        return views;
    }

    private async Task<WithdrawalRequest> GetAsync(Guid id, CancellationToken ct)
    {
        await using var session = store.QuerySession();
        return await session.LoadAsync<WithdrawalRequest>(id, ct)
            ?? throw new WalletDomainException(WalletError.WithdrawalNotFound, $"No existe el retiro {id}.");
    }

    private async Task<WithdrawalRequest> GetOwnedAsync(Guid playerUserId, Guid id, CancellationToken ct)
    {
        var request = await GetAsync(id, ct);
        return request.PlayerUserId == playerUserId
            ? request
            : throw new WalletDomainException(WalletError.WithdrawalNotFound, $"No existe el retiro {id}."); // uno ajeno responde como si no existiera
    }
}
