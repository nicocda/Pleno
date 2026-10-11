using JasperFx;
using Marten;
using Marten.Exceptions;

namespace Casino.Modules.Wallet.Application;

/// <summary>
/// Registro de auditoria de las acciones del personal de backoffice sobre las cuentas: quien, a quien, cuanto y cuando.
/// Solo guarda ids (nunca nombre ni email del administrador ni del jugador). Es de solo-agregar: no se edita ni se borra.
/// </summary>
public sealed class BackofficeAudit(IDocumentStore store, TimeProvider clock)
{
    public const string ChipsCreditedAction = "ChipsCredited";
    public const string ChipsTransferredAction = "ChipsTransferred";

    /// <summary>
    /// Anota una carga de fichas. Es idempotente: la misma carga (misma cuenta y misma clave) se anota una sola vez, asi que se puede
    /// llamar tambien cuando la carga resulto un duplicado. Eso sirve para curar un fallo entre la carga y su anotacion: al reintentar con
    /// la misma clave, la carga se detecta como duplicada y la anotacion que faltaba se completa.
    /// </summary>
    public async Task RecordCreditAsync(
        Guid actorUserId, Guid targetUserId, Guid accountId, long amount, string idempotencyKey, Guid transactionId, CancellationToken ct = default)
    {
        var entry = new AuditEntry
        {
            Id = AuditEntry.BuildId(ChipsCreditedAction, accountId, idempotencyKey),
            Action = ChipsCreditedAction,
            ActorUserId = actorUserId,
            TargetUserId = targetUserId,
            AccountId = accountId,
            Amount = amount,
            IdempotencyKey = idempotencyKey,
            TransactionId = transactionId,
            OccurredAt = clock.GetUtcNow(),
        };

        try
        {
            await using var session = store.LightweightSession();
            session.Insert(entry);
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is DocumentAlreadyExistsException || ex.InnerException is DocumentAlreadyExistsException)
        {
            // Ya estaba anotada: el original se conserva tal cual.
        }
    }

    /// <summary>
    /// Anota una carga de un cajero o jefe a alguien de su jurisdiccion (idempotente, igual que <see cref="RecordCreditAsync"/>). El actor es quien carga y el
    /// destino quien recibe; solo ids.
    /// </summary>
    public async Task RecordTransferAsync(
        Guid actorUserId, Guid targetUserId, Guid targetAccountId, long amount, string idempotencyKey, Guid transactionId, CancellationToken ct = default)
    {
        var entry = new AuditEntry
        {
            Id = AuditEntry.BuildId(ChipsTransferredAction, targetAccountId, $"{actorUserId:N}:{idempotencyKey}"),
            Action = ChipsTransferredAction,
            ActorUserId = actorUserId,
            TargetUserId = targetUserId,
            AccountId = targetAccountId,
            Amount = amount,
            IdempotencyKey = idempotencyKey,
            TransactionId = transactionId,
            OccurredAt = clock.GetUtcNow(),
        };

        try
        {
            await using var session = store.LightweightSession();
            session.Insert(entry);
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is DocumentAlreadyExistsException || ex.InnerException is DocumentAlreadyExistsException)
        {
            // Ya estaba anotada.
        }
    }

    /// <summary>Las ultimas cargas que hizo <paramref name="actorUserId"/> (un cajero ve solo las suyas), las mas recientes primero.</summary>
    public async Task<IReadOnlyList<AuditEntry>> ListTransfersByActorAsync(Guid actorUserId, int limit, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        return await session.Query<AuditEntry>()
            .Where(e => e.Action == ChipsTransferredAction && e.ActorUserId == actorUserId)
            .OrderByDescending(e => e.OccurredAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Historial de cargas con filtros (jugador, desde, hasta) y paginado por cursor de fecha. Devuelve ademas el total de fichas y la
    /// cantidad de cargas que cumplen el filtro (no solo las de la pagina).
    /// </summary>
    public async Task<CreditHistory> QueryCreditsAsync(CreditFilter filter, int limit, DateTimeOffset? before, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        await using var session = store.QuerySession();
        var query = session.Query<AuditEntry>().Where(e => e.Action == ChipsCreditedAction);
        if (filter.TargetUserId is { } target)
        {
            query = query.Where(e => e.TargetUserId == target);
        }

        if (filter.From is { } from)
        {
            query = query.Where(e => e.OccurredAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(e => e.OccurredAt < to);
        }

        var count = await query.CountAsync(ct);
        var total = count == 0 ? 0 : await query.SumAsync(e => e.Amount, ct);

        var page = before is { } cursor ? query.Where(e => e.OccurredAt < cursor) : query;
        var rows = await page.OrderByDescending(e => e.OccurredAt).Take(limit + 1).ToListAsync(ct);
        var items = rows.Take(limit).ToList();
        return new CreditHistory(items, rows.Count > limit ? items[^1].OccurredAt : null, total, count);
    }

    /// <summary>Las anotaciones mas recientes primero.</summary>
    public async Task<IReadOnlyList<AuditEntry>> ListAsync(int limit, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        return await session.Query<AuditEntry>().OrderByDescending(e => e.OccurredAt).Take(limit).ToListAsync(ct);
    }
}

/// <summary>Filtros del historial de cargas. Todos opcionales; <see cref="To"/> es exclusivo.</summary>
public sealed record CreditFilter(Guid? TargetUserId = null, DateTimeOffset? From = null, DateTimeOffset? To = null);

public sealed record CreditHistory(IReadOnlyList<AuditEntry> Items, DateTimeOffset? NextBefore, long TotalAmount, int Count);

public sealed class AuditEntry
{
    public required string Id { get; init; }

    public required string Action { get; init; }

    public required Guid ActorUserId { get; init; }

    public required Guid TargetUserId { get; init; }

    public required Guid AccountId { get; init; }

    public required long Amount { get; init; }

    public required string IdempotencyKey { get; init; }

    public required Guid TransactionId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public static string BuildId(string action, Guid accountId, string key) => $"{action}:{accountId:N}:{key}";
}
