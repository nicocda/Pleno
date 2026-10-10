using System.Security.Cryptography;
using System.Text;
using Casino.BuildingBlocks;
using Casino.Modules.Wallet.Domain;

namespace Casino.Modules.Wallet.Application;

public sealed partial class WalletService
{
    /// <summary>
    /// Transfiere fichas de un jugador a otro (la carga de un cajero, un cobro). Las dos cuentas se escriben en UNA sola transaccion: o se mueven las dos o ninguna,
    /// y la suma de fichas del sistema no cambia. Es idempotente por (cuenta que envia, <paramref name="key"/>): repetir la misma operacion no mueve nada de nuevo.
    /// Quien llama es responsable de haber comprobado que la transferencia esta permitida (jurisdiccion); aca solo se garantiza la contabilidad.
    /// </summary>
    public Task<OperationOutcome> TransferAsync(Guid fromUserId, Guid toUserId, string key, long amount, CancellationToken ct = default)
    {
        if (fromUserId == toUserId)
        {
            throw new WalletDomainException(WalletError.InvalidTransfer, "No se pueden transferir fichas a la misma cuenta.");
        }

        var inKey = InKeyFor(fromUserId, key);
        return BetweenAccountsAsync(
            fromUserId,
            toUserId,
            (from, to, now) =>
            {
                var transferId = Guid.CreateVersion7();
                var outcome = from.TransferOut(key, transferId, toUserId, amount, now);
                if (!outcome.IsDuplicate)
                {
                    to.TransferIn(inKey, transferId, fromUserId, amount, now);
                }

                return outcome;
            },
            ct);
    }

    /// <summary>
    /// Paga una reserva abierta de <paramref name="fromUserId"/> a <paramref name="toUserId"/> (el retiro de un jugador que cobra su cajero): las fichas salen de la reserva y entran a la cuenta
    /// que recibe, en una sola transaccion. Idempotente por (cuenta, <paramref name="key"/>).
    /// </summary>
    public Task<OperationOutcome> PayOutReservationAsync(Guid fromUserId, Guid toUserId, Guid reservationId, string key, CancellationToken ct = default)
    {
        if (fromUserId == toUserId)
        {
            throw new WalletDomainException(WalletError.InvalidTransfer, "No se puede pagar una reserva a la misma cuenta.");
        }

        var inKey = InKeyFor(fromUserId, key);
        return BetweenAccountsAsync(
            fromUserId,
            toUserId,
            (from, to, now) =>
            {
                var stake = from.OpenReservations.GetValueOrDefault(reservationId); // lo que la reserva tiene, antes de cerrarla
                var outcome = from.PayOutReservation(key, reservationId, toUserId, now);
                if (!outcome.IsDuplicate)
                {
                    to.TransferIn(inKey, outcome.TransactionId, fromUserId, stake, now);
                }

                return outcome;
            },
            ct);
    }

    // La mitad de entrada usa su propia clave (derivada) para no chocar con operaciones de la cuenta que recibe que tengan la misma clave.
    private static string InKeyFor(Guid fromUserId, string key) =>
        $"in:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{fromUserId:N}|{key}")))}";

    /// <summary>
    /// Carga las dos cuentas, deja que <paramref name="operation"/> decida en cada una y guarda las dos en UNA transaccion (con reintentos por conflicto de version). Las compuertas por cuenta se
    /// toman siempre en el mismo orden para que dos operaciones cruzadas no se bloqueen entre si. Si la operacion devuelve un duplicado no se escribe nada.
    /// </summary>
    private async Task<OperationOutcome> BetweenAccountsAsync(Guid fromUserId, Guid toUserId, Func<WalletAccount, WalletAccount, DateTimeOffset, OperationOutcome> operation, CancellationToken ct)
    {
        var fromId = PlayerIds.WalletAccountFor(fromUserId);
        var toId = PlayerIds.WalletAccountFor(toUserId);
        var lowGate = Math.Min(GateIndex(fromId), GateIndex(toId));
        var highGate = Math.Max(GateIndex(fromId), GateIndex(toId));

        for (var attempt = 1; ; attempt++)
        {
            TimeSpan retryDelay;
            // Las compuertas se toman siempre en el mismo orden para que dos transferencias cruzadas no se bloqueen entre si.
            await _gates[lowGate].WaitAsync(ct);
            var highHeld = false;
            try
            {
                if (highGate != lowGate)
                {
                    await _gates[highGate].WaitAsync(ct);
                    highHeld = true;
                }

                await using var session = store.LightweightSession();
                var from = await LoadAsync(session, fromId, ct);
                var to = await LoadAsync(session, toId, ct);
                var fromVersion = from.Version;
                var toVersion = to.Version;

                var outcome = operation(from, to, clock.GetUtcNow());
                if (outcome.IsDuplicate)
                {
                    return outcome; // ya se hizo (las dos mitades salen juntas)
                }

                await using var outboxSession = outbox?.Enroll(session);
                await StageAsync(session, outboxSession, fromId, from, fromVersion);
                await StageAsync(session, outboxSession, toId, to, toVersion);

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
                if (highHeld)
                {
                    _gates[highGate].Release();
                }

                _gates[lowGate].Release();
            }

            await Task.Delay(retryDelay, ct);
        }
    }

    private static int GateIndex(Guid accountId) => (accountId.GetHashCode() & int.MaxValue) % GateCount;
}
