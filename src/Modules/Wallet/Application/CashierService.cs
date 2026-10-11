using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Casino.BuildingBlocks;
using Casino.Modules.Wallet.Domain;
using JasperFx;
using Marten;
using Marten.Exceptions;

namespace Casino.Modules.Wallet.Application;

/// <summary>El lugar de una persona en la jerarquia de cargas.</summary>
public enum HierarchyLevel
{
    /// <summary>Jugador comun. Su padre es el cajero que le carga.</summary>
    Player = 0,

    /// <summary>Cajero. Su padre es el jefe de cajeros que le carga.</summary>
    Cashier = 1,

    /// <summary>Jefe de cajeros. Es la raiz: no tiene padre; sus fichas se las da el backoffice.</summary>
    HeadCashier = 2,
}

/// <summary>Un nodo del arbol de cargas: de quien depende cada usuario. Un solo padre por usuario (un arbol, no un grafo).</summary>
public sealed class HierarchyNode
{
    /// <summary>El usuario (tambien es el id del documento).</summary>
    public Guid Id { get; set; }

    public HierarchyLevel Level { get; set; }

    public Guid? ParentUserId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid UpdatedBy { get; set; }

    /// <summary>
    /// El nombre de usuario (el "preferred_username" del token) para que quien tiene a esta persona a cargo pueda distinguirla. Solo se guarda de quienes estan en el arbol
    /// y se renueva cuando la persona usa la API; nunca se muestra fuera de su jurisdiccion.
    /// </summary>
    public string? DisplayName { get; set; }
}

/// <summary>Un jugador (o cajero) de la jurisdiccion de alguien, con su saldo si ya tiene cuenta.</summary>
/// <summary>Lo que dejo una carga: la transaccion, si era un reintento y la comision que cobro quien cargo.</summary>
public sealed record LoadResult(OperationOutcome Outcome, long Commission);

public sealed record JurisdictionMember(Guid UserId, HierarchyLevel Level, string? DisplayName, long? Available, long? Reserved);

/// <summary>
/// Cajeros y jefes de cajeros: arman el arbol de jurisdicciones (lo decide el backoffice) y cargan fichas hacia abajo (jefe → cajero → jugador) desde su
/// PROPIO saldo, por transferencia: las fichas nunca se crean, solo se mueven. La Wallet garantiza la contabilidad; aca vive la regla de quien puede cargarle a quien.
/// </summary>
public sealed class CashierService(IDocumentStore store, WalletService wallet, BackofficeAudit audit, TimeProvider clock, CashierOptions options)
{
    // Serializa el calculo de la comision de una misma persona dentro de esta instancia (que dos cargas a la vez no pasen el tope diario).
    private readonly SemaphoreSlim[] _commissionGates = [.. Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1))];

    private const int MaxDisplayNameLength = 60;

    // Ultimo nombre guardado de cada usuario (en esta instancia): evita ir a la base en cada pedido cuando el nombre no cambio.
    private readonly ConcurrentDictionary<Guid, string> _knownNames = new();

    /// <summary>
    /// Guarda el nombre de usuario de quien esta en el arbol (si no esta, no guarda nada: no se acumulan nombres de quien no los necesita). Nunca falla el pedido que lo dispara.
    /// </summary>
    public async Task TouchDisplayNameAsync(Guid userId, string? rawName, CancellationToken ct = default)
    {
        var name = CleanName(rawName);
        if (name is null || (_knownNames.TryGetValue(userId, out var known) && known == name))
        {
            return;
        }

        try
        {
            await using var session = store.LightweightSession();
            var node = await session.LoadAsync<HierarchyNode>(userId, ct);
            if (node is null)
            {
                return; // no esta en el arbol todavia: se guardara cuando lo ubiquen y vuelva a usar la API
            }

            if (node.DisplayName != name)
            {
                node.DisplayName = name;
                session.Store(node);
                await session.SaveChangesAsync(ct);
            }

            _knownNames[userId] = name;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Mejora de visualizacion: si falla, se reintenta en el proximo pedido.
        }
    }

    private static string? CleanName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return null;
        }

        var name = new string([.. rawName.Trim().Where(c => !char.IsControl(c))]);
        return name.Length == 0 ? null : name[..Math.Min(name.Length, MaxDisplayNameLength)];
    }

    /// <summary>El nivel que da un conjunto de roles del token (el mas alto manda). Null si no es cajero ni jefe.</summary>
    public static HierarchyLevel? LevelOfRoles(IEnumerable<string> roles)
    {
        var list = roles as IReadOnlyCollection<string> ?? [.. roles];
        if (list.Contains(Roles.HeadCashier))
        {
            return HierarchyLevel.HeadCashier;
        }

        return list.Contains(Roles.Cashier) ? HierarchyLevel.Cashier : null;
    }

    public static string NameOf(HierarchyLevel level) => level switch
    {
        HierarchyLevel.HeadCashier => Roles.HeadCashier,
        HierarchyLevel.Cashier => Roles.Cashier,
        _ => Roles.Player,
    };

    public static bool TryParseLevel(string? name, out HierarchyLevel level)
    {
        switch (name)
        {
            case Roles.HeadCashier:
                level = HierarchyLevel.HeadCashier;
                return true;
            case Roles.Cashier:
                level = HierarchyLevel.Cashier;
                return true;
            case Roles.Player:
                level = HierarchyLevel.Player;
                return true;
            default:
                level = default;
                return false;
        }
    }

    // ---- Backoffice: armar el arbol ----

    /// <summary>
    /// Pone a <paramref name="userId"/> en el arbol con ese nivel y ese padre. Reglas: el jefe no tiene padre; el padre de un cajero es un jefe y el de un jugador es un cajero;
    /// no se cambia el nivel de quien ya tiene gente a cargo (se mueve primero a su gente).
    /// </summary>
    public async Task<HierarchyNode> AssignAsync(Guid actorUserId, Guid userId, HierarchyLevel level, Guid? parentUserId, CancellationToken ct = default)
    {
        if (parentUserId == userId)
        {
            throw new WalletDomainException(WalletError.InvalidHierarchy, "Nadie puede depender de si mismo.");
        }

        await using var session = store.LightweightSession();
        if (level == HierarchyLevel.HeadCashier)
        {
            if (parentUserId is not null)
            {
                throw new WalletDomainException(WalletError.InvalidHierarchy, "El jefe de cajeros no tiene padre: sus fichas se las da el backoffice.");
            }
        }
        else if (parentUserId is { } parentId)
        {
            var parent = await session.LoadAsync<HierarchyNode>(parentId, ct);
            if (parent is null || parent.Level != level + 1)
            {
                throw new WalletDomainException(
                    WalletError.InvalidHierarchy,
                    level == HierarchyLevel.Cashier ? "El padre de un cajero tiene que ser un jefe de cajeros." : "El padre de un jugador tiene que ser un cajero.");
            }
        }

        var existing = await session.LoadAsync<HierarchyNode>(userId, ct);
        if (existing is not null && existing.Level != level && await session.Query<HierarchyNode>().AnyAsync(n => n.ParentUserId == userId, ct))
        {
            throw new WalletDomainException(WalletError.InvalidHierarchy, "Tiene gente a su cargo: movela antes de cambiarle el nivel.");
        }

        var node = existing ?? new HierarchyNode { Id = userId };
        node.Level = level;
        node.ParentUserId = parentUserId;
        node.UpdatedAt = clock.GetUtcNow();
        node.UpdatedBy = actorUserId;
        session.Store(node);
        await session.SaveChangesAsync(ct);
        return node;
    }

    public async Task<IReadOnlyList<HierarchyNode>> ListAllAsync(CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        return await session.Query<HierarchyNode>().OrderBy(n => n.Level).ToListAsync(ct);
    }

    public async Task<HierarchyNode?> GetNodeAsync(Guid userId, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        return await session.LoadAsync<HierarchyNode>(userId, ct);
    }

    // ---- Cajero / jefe ----

    /// <summary>Los nombres de la gente a cargo directo de <paramref name="parentUserId"/> (solo de ellos, nunca de otros).</summary>
    public async Task<IReadOnlyDictionary<Guid, string?>> GetMemberNamesAsync(Guid parentUserId, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        var nodes = await session.Query<HierarchyNode>().Where(n => n.ParentUserId == parentUserId).ToListAsync(ct);
        return nodes.ToDictionary(n => n.Id, n => n.DisplayName);
    }

    /// <summary>La gente a cargo directo de <paramref name="parentUserId"/>, con su saldo.</summary>
    public async Task<IReadOnlyList<JurisdictionMember>> ListMembersAsync(Guid parentUserId, int limit, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        var nodes = await session.Query<HierarchyNode>().Where(n => n.ParentUserId == parentUserId).OrderBy(n => n.UpdatedAt).Take(limit).ToListAsync(ct);
        var members = new List<JurisdictionMember>(nodes.Count);
        foreach (var node in nodes)
        {
            try
            {
                var account = await wallet.GetAsync(PlayerIds.WalletAccountFor(node.Id), ct);
                members.Add(new JurisdictionMember(node.Id, node.Level, node.DisplayName, account.Available, account.Reserved));
            }
            catch (WalletDomainException ex) when (ex.Error == WalletError.AccountNotFound)
            {
                members.Add(new JurisdictionMember(node.Id, node.Level, node.DisplayName, null, null)); // todavia no abrio su cuenta
            }
        }

        return members;
    }

    /// <summary>
    /// Carga fichas desde el saldo de quien llama hacia alguien de su jurisdiccion DIRECTA: el jefe solo a sus cajeros y el cajero solo a sus jugadores.
    /// El nivel de quien llama sale de su token (rol) y tiene que coincidir con el que le asigno el backoffice en el arbol.
    /// </summary>
    public async Task<LoadResult> LoadChipsAsync(Guid actorUserId, IEnumerable<string> actorRoles, Guid targetUserId, long amount, string idempotencyKey, CancellationToken ct = default)
    {
        var actorLevel = LevelOfRoles(actorRoles)
            ?? throw new WalletDomainException(WalletError.NotInJurisdiction, "Tu usuario no es cajero ni jefe de cajeros.");

        var actor = await GetNodeAsync(actorUserId, ct);
        if (actor is null || actor.Level != actorLevel)
        {
            throw new WalletDomainException(WalletError.NotInJurisdiction, "Todavia no te asignaron un lugar en la jerarquia de cajeros.");
        }

        var target = await GetNodeAsync(targetUserId, ct);
        if (target is null || target.ParentUserId != actorUserId || target.Level != actorLevel - 1)
        {
            throw new WalletDomainException(
                WalletError.NotInJurisdiction,
                actorLevel == HierarchyLevel.HeadCashier ? "Solo podes cargarle a los cajeros de tu jurisdiccion." : "Solo podes cargarle a los jugadores de tu jurisdiccion.");
        }

        var outcome = await wallet.TransferAsync(actorUserId, targetUserId, idempotencyKey, amount, ct);
        // Tambien si fue un duplicado: asi un reintento completa la anotacion y la comision si fallo la primera vez.
        await audit.RecordTransferAsync(actorUserId, targetUserId, PlayerIds.WalletAccountFor(targetUserId), amount, idempotencyKey, outcome.TransactionId, ct);
        var commission = await PayCommissionAsync(actorUserId, actorLevel, targetUserId, amount, idempotencyKey, outcome.TransactionId, ct);
        return new LoadResult(outcome, commission);
    }

    /// <summary>La comision (en milesimas) que cobra hoy un nivel; el front la muestra.</summary>
    public int CommissionPermilleFor(HierarchyLevel level) => options.PermilleFor(level);

    /// <summary>
    /// Paga la comision de una carga: la casa le acredita a quien cargo un porcentaje (con tope por carga y por dia). La decision se guarda ANTES de pagar, asi un reintento
    /// (duplicado de la carga, caida entre la transferencia y el pago) paga exactamente lo mismo y nunca dos veces: el credito es idempotente por esa misma decision.
    /// </summary>
    private async Task<long> PayCommissionAsync(Guid actorUserId, HierarchyLevel actorLevel, Guid targetUserId, long loaded, string loadKey, Guid transferTransactionId, CancellationToken ct)
    {
        var id = CommissionRecord.BuildId(actorUserId, loadKey);
        var gate = _commissionGates[(actorUserId.GetHashCode() & int.MaxValue) % _commissionGates.Length];
        await gate.WaitAsync(ct);
        try
        {
            CommissionRecord? record;
            await using (var read = store.QuerySession())
            {
                record = await read.LoadAsync<CommissionRecord>(id, ct);
            }

            if (record is null)
            {
                var now = clock.GetUtcNow();
                var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
                long today;
                await using (var read = store.QuerySession())
                {
                    var todays = read.Query<CommissionRecord>().Where(c => c.ActorUserId == actorUserId && c.OccurredAt >= dayStart);
                    today = await todays.CountAsync(ct) == 0 ? 0 : await todays.SumAsync(c => c.Amount, ct);
                }

                record = new CommissionRecord
                {
                    Id = id,
                    ActorUserId = actorUserId,
                    TargetUserId = targetUserId,
                    LoadedAmount = loaded,
                    Amount = CashierCommissionMath.Compute(loaded, options.PermilleFor(actorLevel), options.MaxCommissionPerLoad, options.MaxCommissionPerDay - today),
                    TransferTransactionId = transferTransactionId,
                    OccurredAt = now,
                };
                try
                {
                    await using var write = store.LightweightSession();
                    write.Insert(record);
                    await write.SaveChangesAsync(ct);
                }
                catch (Exception ex) when (ex is DocumentAlreadyExistsException || ex.InnerException is DocumentAlreadyExistsException)
                {
                    await using var again = store.QuerySession();
                    record = await again.LoadAsync<CommissionRecord>(id, ct) ?? record; // otra instancia decidio primero: vale esa
                }
            }

            if (record.Amount > 0)
            {
                var creditKey = "commission:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
                await wallet.CreditAsync(PlayerIds.WalletAccountFor(actorUserId), creditKey, record.Amount, ct);
            }

            return record.Amount;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Las comisiones que pago la casa, las mas recientes primero, con el total emitido (backoffice).</summary>
    public async Task<(IReadOnlyList<CommissionRecord> Items, long Total)> ListCommissionsAsync(int limit, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        var items = await session.Query<CommissionRecord>().OrderByDescending(c => c.OccurredAt).Take(limit).ToListAsync(ct);
        var all = session.Query<CommissionRecord>();
        var total = await all.CountAsync(ct) == 0 ? 0 : await all.SumAsync(c => c.Amount, ct);
        return (items, total);
    }
}
