using System.Security.Cryptography;
using System.Text;
using Casino.BuildingBlocks;
using Casino.Contracts;
using Casino.Modules.Games.Platform;
using Casino.Modules.Games.Slots;
using Marten;
using Microsoft.Extensions.Logging;

namespace Casino.Modules.Games.Application;

/// <summary>
/// Coordina la vida de un giro de tragamonedas. Es el mismo recorrido que la ruleta y reutiliza la Wallet, el nonce y las seeds
/// sin cambios: Colocar -> (Wallet reserva) -> sortear -> (Wallet liquida) -> cerrar.
/// </summary>
public sealed partial class SlotsService(
    IDocumentStore store,
    FairnessService fairness,
    IOutboxFactory outbox,
    TimeProvider clock,
    SlotsSettingsStore settings,
    ILogger<SlotsService> logger) : IGameRounds
{
    /// <summary>La tabla de pagos vigente y su version.</summary>
    public Task<SlotsSettingsSnapshot> GetSettingsAsync(CancellationToken ct = default) => settings.GetCurrentAsync(ct);

    /// <summary>
    /// Coloca un giro. En UNA transaccion: asigna el nonce (lo decide el servidor), guarda el giro y encola la orden de reserva
    /// a la Wallet. Es idempotente: la misma IdempotencyKey de la misma cuenta devuelve el mismo giro.
    /// </summary>
    public async Task<PlacedBet> PlaceSpinAsync(Guid userId, Guid accountId, long stake, string idempotencyKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
        {
            throw new GamesDomainException(GamesError.InvalidIdempotencyKey, "La IdempotencyKey es obligatoria y de hasta 128 caracteres.");
        }

        var paytable = (await settings.GetCurrentAsync(ct)).Paytable;
        if (stake < paytable.MinStake || stake > paytable.MaxStake)
        {
            throw new GamesDomainException(GamesError.InvalidBet, $"La apuesta va de {paytable.MinStake} a {paytable.MaxStake} fichas.");
        }

        var betId = BetIdFor(accountId, idempotencyKey);
        var now = clock.GetUtcNow();

        var allocation = await fairness.AllocateNonceAsync(
            userId,
            betId,
            async (session, outboxSession, nonce) =>
            {
                session.Insert(new SlotsSpin
                {
                    Id = betId,
                    UserId = userId,
                    AccountId = accountId,
                    Stake = stake,
                    PairId = nonce.PairId,
                    Nonce = nonce.Nonce,
                    Status = RoundStatus.Placed,
                    PlacedAt = now,
                });

                await outboxSession.PublishAsync(new ReserveStake(betId, accountId, stake, GameId: "slots"));
            },
            ct);

        if (allocation.AlreadyAllocated)
        {
            // Reintento: el giro ya existe. Si la misma clave llega con otro contenido, es un error del cliente.
            var existing = await GetSpinAsync(betId, ct);
            if (existing.AccountId != accountId || existing.UserId != userId || existing.Stake != stake)
            {
                throw new GamesDomainException(GamesError.BetKeyReused, "La IdempotencyKey ya se uso con otra apuesta.");
            }

            return new PlacedBet(betId, existing.Status, allocation.Nonce, allocation.Commitment, allocation.ClientSeed, AlreadyPlaced: true);
        }

        return new PlacedBet(betId, RoundStatus.Placed, allocation.Nonce, allocation.Commitment, allocation.ClientSeed, AlreadyPlaced: false);
    }

    public async Task<SlotsSpin> GetSpinAsync(Guid betId, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        return await session.LoadAsync<SlotsSpin>(betId, ct)
            ?? throw new GamesDomainException(GamesError.RoundNotFound, $"No existe el giro {betId}.");
    }

    /// <summary>Ultimos giros de un jugador, del mas reciente al mas viejo.</summary>
    public async Task<IReadOnlyList<SlotsSpin>> GetHistoryAsync(Guid userId, int limit, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        return await session.Query<SlotsSpin>()
            .Where(spin => spin.UserId == userId)
            .OrderByDescending(spin => spin.PlacedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    /// <summary>La Wallet reservo las fichas: se sortea con el nonce ya asignado y se informa el resultado.</summary>
    public async Task OnStakeReservedAsync(StakeReserved message, CancellationToken ct = default)
    {
        await using var session = store.LightweightSession();
        var spin = await session.LoadAsync<SlotsSpin>(message.BetId, ct);
        if (spin is null)
        {
            return; // No es un giro de tragamonedas (lo atiende otro juego).
        }

        if (spin.Status != RoundStatus.Placed)
        {
            return; // Entrega duplicada: ya se sorteo.
        }

        var inputs = await fairness.GetDrawInputsAsync(spin.UserId, spin.Id, ct);
        var current = await settings.GetCurrentAsync(ct);
        var outcome = SlotsGame.Play(current.Paytable, spin.Stake, inputs.ServerSeed, inputs.ClientSeed, inputs.Nonce);
        spin.MarkResolved(outcome.Reels, outcome.Multiplier, outcome.Payout, current.Version, outcome.Steps);
        session.Store(spin);

        // El resultado y el aviso a la Wallet salen en la misma transaccion: no puede quedar sorteado sin avisar.
        await using var outboxSession = outbox.Enroll(session);
        await outboxSession.PublishAsync(new RoundResolved(spin.Id, spin.AccountId, outcome.Payout));
        await session.SaveChangesAsync(ct);
    }

    public Task OnStakeSettledAsync(StakeSettled message, CancellationToken ct = default) =>
        CloseAsync(message.BetId, (spin, now) => spin.MarkSettled(now), ct);

    public Task OnStakeRejectedAsync(StakeRejected message, CancellationToken ct = default) =>
        CloseAsync(message.BetId, (spin, now) => spin.MarkRejected(message.Reason, now), ct);

    public Task OnStakeReleasedAsync(StakeReleased message, CancellationToken ct = default) =>
        CloseAsync(message.BetId, (spin, now) => spin.MarkVoided("ReservationExpired", now), ct);

    public Task OnSettlementRejectedAsync(StakeSettlementRejected message, CancellationToken ct = default) =>
        CloseAsync(message.BetId, (spin, now) => spin.MarkVoided(message.Reason, now), ct);

    private async Task CloseAsync(Guid betId, Func<SlotsSpin, DateTimeOffset, bool> transition, CancellationToken ct)
    {
        await using var session = store.LightweightSession();
        var spin = await session.LoadAsync<SlotsSpin>(betId, ct);
        if (spin is null)
        {
            return; // No es un giro de tragamonedas.
        }

        if (transition(spin, clock.GetUtcNow()))
        {
            session.Store(spin);

            // El aviso de "ronda cerrada" sale en la misma transaccion que el cierre, y solo cuando la transicion realmente ocurre.
            await using var outboxSession = outbox.Enroll(session);
            await outboxSession.PublishAsync(new RoundClosed(
                spin.Id,
                spin.AccountId,
                "Slots",
                spin.Status.ToString(),
                WinningNumber: null,
                spin.Stake,
                spin.Status == RoundStatus.Settled ? spin.Payout ?? 0 : 0,
                spin.FailureReason));
            await session.SaveChangesAsync(ct);
        }

        // Siempre (aunque la transicion ya estuviera aplicada): un reintento completa el cierre en el generador si un intento anterior cayo a medias.
        if (spin.IsClosed)
        {
            await fairness.CompleteBetAsync(spin.UserId, spin.Id, ct);
            LogClosed(logger, spin.Id, spin.Status);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Giro {BetId} cerrado: {Status}.")]
    private static partial void LogClosed(ILogger logger, Guid betId, RoundStatus status);

    /// <summary>Id de apuesta determinista por (cuenta, clave): reintentar con la misma clave apunta al mismo giro.</summary>
    internal static Guid BetIdFor(Guid accountId, string idempotencyKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"slots:{accountId:N}:{idempotencyKey}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
