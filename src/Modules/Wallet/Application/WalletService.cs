using System.Net.Sockets;
using Casino.BuildingBlocks;
using Casino.Contracts;
using Casino.Modules.Wallet.Domain;
using Casino.Modules.Wallet.Infrastructure;
using JasperFx;
using JasperFx.Events;
using Marten;
using Marten.Exceptions;
using Npgsql;

namespace Casino.Modules.Wallet.Application;

/// <summary>Saldo reconstruido a una fecha, a partir de los eventos (nunca de snapshots).</summary>
public sealed record BalanceAtDate(
    Guid AccountId,
    DateTimeOffset AsOf,
    long Available,
    long Reserved,
    long Total,
    long EventsApplied);

public sealed partial class WalletService(
    IDocumentStore store,
    TimeProvider clock,
    int maxAttempts = WalletService.DefaultMaxAttempts,
    int snapshotEvery = WalletService.DefaultSnapshotEvery,
    IOutboxFactory? outbox = null,
    TimeSpan? reservationTtl = null)
{
    public const int DefaultMaxAttempts = 20;
    public const int DefaultSnapshotEvery = 50;
    public static readonly TimeSpan DefaultReservationTtl = TimeSpan.FromSeconds(60);

    private const int GateCount = 256;

    // Compuertas por cuenta (striping): serializan las operaciones de una misma cuenta dentro de ESTA instancia
    // para no desperdiciar reintentos. Son por instancia a proposito: entre instancias la garantia es la
    // concurrencia optimista de la base de datos.
    private readonly SemaphoreSlim[] _gates = [.. Enumerable.Range(0, GateCount).Select(_ => new SemaphoreSlim(1, 1))];

    /// <summary>
    /// Abre la cuenta del usuario. El id se deriva del usuario (una cuenta por usuario) y la operacion es idempotente:
    /// abrir dos veces, o dos peticiones a la vez, deja una sola cuenta y devuelve el mismo id.
    /// </summary>
    public async Task<Guid> OpenAccountAsync(Guid userId, CancellationToken ct = default)
    {
        var accountId = PlayerIds.WalletAccountFor(userId);

        await using (var read = store.QuerySession())
        {
            if (await read.Events.FetchStreamStateAsync(accountId, ct) is not null)
            {
                return accountId;
            }
        }

        var account = WalletAccount.Open(accountId, userId, clock.GetUtcNow());
        try
        {
            await using var session = store.LightweightSession();
            session.Events.StartStream(accountId, account.UncommittedEvents.ToArray<object>());
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsConcurrencyConflict(ex) || IsStreamCollision(ex))
        {
            // Otra peticion la abrio en el mismo instante: la cuenta ya existe, que es lo que se queria.
        }

        return accountId;
    }

    public Task<OperationOutcome> CreditAsync(Guid accountId, string key, long amount, CancellationToken ct = default) =>
        ExecuteAsync(accountId, a => a.Credit(key, amount, clock.GetUtcNow()), ct);

    /// <param name="gameId">Juego donde se aposto; solo informativo (se muestra en el extracto).</param>
    public Task<OperationOutcome> ReserveAsync(Guid accountId, string key, Guid reservationId, long stake, string? gameId = null, CancellationToken ct = default) =>
        ExecuteAsync(accountId, a => a.Reserve(key, reservationId, stake, clock.GetUtcNow(), gameId), ct);

    /// <param name="ttl">Cuanto puede quedar abierta esta reserva antes de liberarse sola (un juego de ronda larga pide mas que el plazo por defecto).</param>
    /// <param name="gameId">Juego donde se aposto; solo informativo (se muestra en el extracto).</param>
    public Task<OperationOutcome> ReserveAsync(Guid accountId, string key, Guid reservationId, long stake, TimeSpan ttl, string? gameId = null, CancellationToken ct = default) =>
        ExecuteAsync(accountId, a => a.Reserve(key, reservationId, stake, clock.GetUtcNow(), gameId), ct, ttl);

    public Task<OperationOutcome> SettleAsync(Guid accountId, string key, Guid reservationId, long payout, CancellationToken ct = default) =>
        ExecuteAsync(accountId, a => a.Settle(key, reservationId, payout, clock.GetUtcNow()), ct);

    public Task<OperationOutcome> ReleaseAsync(Guid accountId, string key, Guid reservationId, CancellationToken ct = default) =>
        ExecuteAsync(accountId, a => a.Release(key, reservationId, clock.GetUtcNow()), ct);

    public Task<OperationOutcome> ReverseAsync(Guid accountId, string key, Guid transactionId, CancellationToken ct = default) =>
        ExecuteAsync(accountId, a => a.Reverse(key, transactionId, clock.GetUtcNow()), ct);

    public async Task<WalletAccount> GetAsync(Guid accountId, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        return await LoadAsync(session, accountId, ct);
    }

    /// <summary>
    /// Auditoria: reconstruye el saldo aplicando solo los eventos ocurridos hasta <paramref name="asOf"/> (inclusive).
    /// Ignora los snapshots a proposito: se recalcula desde la historia. Devuelve null si la cuenta aun no existia.
    /// </summary>
    public async Task<BalanceAtDate?> GetBalanceAtAsync(Guid accountId, DateTimeOffset asOf, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        var stream = await session.Events.FetchStreamAsync(accountId, token: ct);
        if (stream.Count == 0)
        {
            throw new WalletDomainException(WalletError.AccountNotFound, $"No existe la cuenta {accountId}.");
        }

        var history = stream
            .Select(e => e.Data)
            .OfType<WalletEvent>()
            .TakeWhile(e => e.OccurredAt <= asOf)
            .ToList();

        if (history.Count == 0)
        {
            return null;
        }

        var account = WalletAccount.Rehydrate(history);
        return new BalanceAtDate(accountId, asOf, account.Available, account.Reserved, account.Available + account.Reserved, history.Count);
    }

    /// <summary>
    /// Carga, decide, y guarda con concurrencia optimista. Si otro escribio primero (o la conexion fallo),
    /// recarga y reintenta con la MISMA IdempotencyKey, asi un commit incierto nunca duplica la operacion.
    /// </summary>
    private async Task<OperationOutcome> ExecuteAsync(Guid accountId, Func<WalletAccount, OperationOutcome> operation, CancellationToken ct, TimeSpan? ttl = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            TimeSpan retryDelay;
            var gate = _gates[(accountId.GetHashCode() & int.MaxValue) % GateCount];
            await gate.WaitAsync(ct);
            try
            {
                await using var session = store.LightweightSession();
                var account = await LoadAsync(session, accountId, ct);
                var loadedVersion = account.Version;

                var outcome = operation(account);
                if (outcome.IsDuplicate)
                {
                    return outcome;
                }

                // Outbox: los hechos se guardan en la MISMA transaccion que los eventos. Si el commit falla no se publica
                // nada; si el commit sale bien, el mensaje esta garantizado aunque RabbitMQ este caido en ese momento.
                await using var outboxSession = outbox?.Enroll(session);
                await StageAsync(session, outboxSession, accountId, account, loadedVersion, ttl);

                await session.SaveChangesAsync(ct);
                return outcome;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsRetryable(ex) && !ct.IsCancellationRequested)
            {
                WalletTelemetry.OperationRetries.Add(
                    1,
                    new KeyValuePair<string, object?>(
                        WalletTelemetry.ReasonTag,
                        IsConcurrencyConflict(ex) ? WalletTelemetry.ConflictReason : WalletTelemetry.TransientReason));
                retryDelay = BackoffFor(attempt);
            }
            finally
            {
                gate.Release();
            }

            await Task.Delay(retryDelay, ct);
        }
    }

    /// <summary>
    /// Deja listo para guardar lo que hizo una cuenta: sus eventos (con la version esperada), las claves de idempotencia, el snapshot que toque y los
    /// mensajes del outbox. No hace el commit: lo hace quien llama, asi varias cuentas pueden salir en una sola transaccion.
    /// </summary>
    private async Task StageAsync(IDocumentSession session, IOutboxSession? outboxSession, Guid accountId, WalletAccount account, long loadedVersion, TimeSpan? ttl = null)
    {
        var newEvents = account.UncommittedEvents.ToArray<object>();
        session.Events.Append(accountId, loadedVersion + newEvents.Length, newEvents);
        foreach (var ledger in account.UncommittedEvents.OfType<LedgerEvent>())
        {
            session.Insert(new IdempotencyRecord
            {
                Id = IdempotencyRecord.BuildId(accountId, ledger.IdempotencyKey),
                AccountId = accountId,
                Key = ledger.IdempotencyKey,
                TransactionId = ledger.TransactionId,
                Fingerprint = ledger.Fingerprint,
                RecordedAt = ledger.OccurredAt,
            });
        }

        if (snapshotEvery > 0 && account.Version / snapshotEvery > loadedVersion / snapshotEvery)
        {
            // Mismo commit que los eventos: el snapshot nunca queda adelantado ni atrasado respecto del stream.
            session.Store(account.ToSnapshot());
        }

        if (outboxSession is not null)
        {
            foreach (var message in WalletIntegrationEvents.From(accountId, account.UncommittedEvents, account))
            {
                await outboxSession.PublishAsync(message);
            }

            // Compensacion por vencimiento: si nadie liquida la reserva a tiempo, la Wallet la libera sola.
            foreach (var reserved in account.UncommittedEvents.OfType<BetReserved>())
            {
                await outboxSession.PublishAsync(
                    new ExpireReservation(reserved.ReservationId, accountId),
                    ttl ?? reservationTtl ?? DefaultReservationTtl);
            }
        }
    }

    private static async Task<WalletAccount> LoadAsync(IQuerySession session, Guid accountId, CancellationToken ct)
    {
        var snapshot = await session.LoadAsync<WalletSnapshot>(accountId, ct);
        var fromVersion = snapshot is null ? 0 : snapshot.Version + 1;

        var tail = await session.Events.FetchStreamAsync(accountId, fromVersion: fromVersion, token: ct);
        if (snapshot is null && tail.Count == 0)
        {
            throw new WalletDomainException(WalletError.AccountNotFound, $"No existe la cuenta {accountId}.");
        }

        var events = tail.Select(e => e.Data).OfType<WalletEvent>();
        return snapshot is null ? WalletAccount.Rehydrate(events) : WalletAccount.Restore(snapshot, events);
    }

    private static TimeSpan BackoffFor(int attempt) =>
        TimeSpan.FromMilliseconds(Random.Shared.Next(5, 20) * Math.Min(attempt, 8));

    private static bool IsStreamCollision(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is JasperFx.Events.ExistingStreamIdCollisionException
                or Marten.Exceptions.ExistingStreamIdCollisionException
                or PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsConcurrencyConflict(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is EventStreamUnexpectedMaxEventIdException or ConcurrencyException or DocumentAlreadyExistsException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Conflictos de version y fallos transitorios de conexion. Los errores de negocio nunca se reintentan.</summary>
    private static bool IsRetryable(Exception ex)
    {
        if (IsConcurrencyConflict(ex))
        {
            return true;
        }

        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException
                or IOException
                or SocketException
                or NpgsqlException { IsTransient: true })
            {
                return true;
            }
        }

        return false;
    }
}
