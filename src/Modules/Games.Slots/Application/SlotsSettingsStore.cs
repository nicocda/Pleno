using Casino.Modules.Games.Slots;
using JasperFx;
using Marten;
using Marten.Exceptions;

namespace Casino.Modules.Games.Application;

/// <summary>La tabla de pagos vigente y el numero de version con el que se guardo (0 = la de la configuracion).</summary>
public sealed record SlotsSettingsSnapshot(int Version, SlotsPaytable Paytable);

/// <summary>
/// Dueno de la tabla de pagos de la tragamonedas. Parte de la configuracion (version 0) y el administrador puede publicar versiones
/// nuevas: cada una se valida con las mismas reglas que al arrancar (el retorno nunca supera el 100%) y queda guardada con quien
/// y cuando. La vigente se cachea unos segundos para no consultar la base en cada giro; otras instancias de la API la ven cambiar
/// en ese plazo.
/// </summary>
public sealed class SlotsSettingsStore(IDocumentStore store, SlotsPaytable baseline, TimeProvider clock)
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private SlotsSettingsSnapshot? _cached;
    private DateTimeOffset _cachedAt;

    /// <summary>La tabla de la configuracion: el punto de partida y a lo que se puede volver.</summary>
    public SlotsPaytable Baseline => baseline;

    public async Task<SlotsSettingsSnapshot> GetCurrentAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_cached is not null && clock.GetUtcNow() - _cachedAt < CacheTtl)
            {
                return _cached;
            }
        }

        var fresh = await LoadLatestAsync(ct);
        Remember(fresh);
        return fresh;
    }

    /// <summary>La tabla de una version concreta (para recalcular un giro viejo). Null si esa version no existe.</summary>
    public async Task<SlotsSettingsSnapshot?> GetVersionAsync(int version, CancellationToken ct = default)
    {
        if (version == 0)
        {
            return new SlotsSettingsSnapshot(0, baseline);
        }

        await using var session = store.QuerySession();
        var doc = await session.LoadAsync<SlotsSettingsVersion>(SlotsSettingsVersion.BuildId(version), ct);
        return doc is null ? null : new SlotsSettingsSnapshot(doc.Version, doc.ToPaytable());
    }

    /// <summary>
    /// Publica una version nueva. <paramref name="baseVersion"/> es la version que el administrador estaba mirando: si otro
    /// publico mientras tanto, se rechaza (conflicto) en lugar de pisar su cambio sin saberlo.
    /// </summary>
    public async Task<SlotsSettingsSnapshot> SaveAsync(Guid changedBy, int baseVersion, SlotsPaytable candidate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var current = await LoadLatestAsync(ct); // sin cache: la decision tiene que ser sobre lo ultimo que hay
        if (current.Version != baseVersion)
        {
            throw new GamesDomainException(GamesError.SettingsConflict, $"Otra persona ya publico la version {current.Version}. Recargá los ajustes y volvé a intentar.");
        }

        var next = current.Version + 1;
        try
        {
            await using var session = store.LightweightSession();
            session.Insert(SlotsSettingsVersion.From(next, candidate, changedBy, clock.GetUtcNow()));
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is DocumentAlreadyExistsException || ex.InnerException is DocumentAlreadyExistsException)
        {
            // Dos administradores publicaron a la vez: gana el primero, el segundo ve el conflicto.
            throw new GamesDomainException(GamesError.SettingsConflict, "Otra persona publicó ajustes al mismo tiempo. Recargá y volvé a intentar.");
        }

        var saved = new SlotsSettingsSnapshot(next, candidate);
        Remember(saved);
        return saved;
    }

    /// <summary>Versiones publicadas, de la mas reciente a la mas vieja.</summary>
    public async Task<IReadOnlyList<SlotsSettingsVersion>> GetHistoryAsync(int limit, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        return await session.Query<SlotsSettingsVersion>().OrderByDescending(v => v.Version).Take(limit).ToListAsync(ct);
    }

    private async Task<SlotsSettingsSnapshot> LoadLatestAsync(CancellationToken ct)
    {
        await using var session = store.QuerySession();
        var latest = await session.Query<SlotsSettingsVersion>().OrderByDescending(v => v.Version).FirstOrDefaultAsync(ct);
        return latest is null ? new SlotsSettingsSnapshot(0, baseline) : new SlotsSettingsSnapshot(latest.Version, latest.ToPaytable());
    }

    private void Remember(SlotsSettingsSnapshot snapshot)
    {
        lock (_gate)
        {
            _cached = snapshot;
            _cachedAt = clock.GetUtcNow();
        }
    }
}
