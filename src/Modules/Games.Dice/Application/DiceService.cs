using System.Security.Cryptography;
using System.Text;
using Casino.BuildingBlocks;
using Casino.Contracts;
using Casino.Modules.Games.Application;
using Casino.Modules.Games.Platform;
using Marten;
using Microsoft.Extensions.Logging;

namespace Casino.Modules.Games.Dice.Application;

public sealed record PlaceDiceBetRequest(Guid UserId, Guid AccountId, DiceDirection Direction, int Chance, long Stake);

/// <summary>
/// Coordina la vida de una tirada de dados, con el mismo protocolo de rondas que la ruleta: colocar -> (Wallet reserva) -> sortear ->
/// (Wallet liquida) -> cerrar; si la Wallet rechaza o vence la reserva, se cierra anulada. Todos los pasos son idempotentes.
/// </summary>
public sealed partial class DiceService(
    IDocumentStore store,
    FairnessService fairness,
    IOutboxFactory outbox,
    TimeProvider clock,
    ILogger<DiceService> logger) : IGameRounds
{
    /// <summary>Coloca una tirada: en UNA transaccion asigna el nonce (lo decide el servidor), guarda la ronda y encola la reserva. Idempotente por (cuenta, clave).</summary>
    public async Task<PlacedBet> PlaceBetAsync(PlaceDiceBetRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
        {
            throw new GamesDomainException(GamesError.InvalidIdempotencyKey, "La IdempotencyKey es obligatoria y de hasta 128 caracteres.");
        }

        if (!DiceBet.TryCreate(request.Direction, request.Chance, request.Stake, out _))
        {
            throw new GamesDomainException(
                GamesError.InvalidBet,
                $"Apuesta invalida: la probabilidad va de {DiceBet.MinChance / 100.0:0.##} % a {DiceBet.MaxChance / 100.0:0.##} % y la apuesta de {DiceBet.MinStake} a {DiceBet.MaxStake} fichas.");
        }

        var betId = BetIdFor(request.AccountId, idempotencyKey);
        var now = clock.GetUtcNow();

        var allocation = await fairness.AllocateNonceAsync(
            request.UserId,
            betId,
            async (session, outboxSession, nonce) =>
            {
                session.Insert(new DiceRound
                {
                    Id = betId,
                    UserId = request.UserId,
                    AccountId = request.AccountId,
                    Direction = request.Direction,
                    Chance = request.Chance,
                    Stake = request.Stake,
                    PairId = nonce.PairId,
                    Nonce = nonce.Nonce,
                    Status = RoundStatus.Placed,
                    PlacedAt = now,
                });

                await outboxSession.PublishAsync(new ReserveStake(betId, request.AccountId, request.Stake, GameId: "dice"));
            },
            ct);

        if (allocation.AlreadyAllocated)
        {
            var existing = await GetRoundAsync(betId, ct);
            if (existing.AccountId != request.AccountId || existing.UserId != request.UserId || existing.Stake != request.Stake
                || existing.Direction != request.Direction || existing.Chance != request.Chance)
            {
                throw new GamesDomainException(GamesError.BetKeyReused, "La IdempotencyKey ya se uso con otra apuesta.");
            }

            return new PlacedBet(betId, existing.Status, allocation.Nonce, allocation.Commitment, allocation.ClientSeed, AlreadyPlaced: true);
        }

        return new PlacedBet(betId, RoundStatus.Placed, allocation.Nonce, allocation.Commitment, allocation.ClientSeed, AlreadyPlaced: false);
    }

    public async Task<DiceRound> GetRoundAsync(Guid betId, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        return await session.LoadAsync<DiceRound>(betId, ct)
            ?? throw new GamesDomainException(GamesError.RoundNotFound, $"No existe la ronda {betId}.");
    }

    /// <summary>Ultimas tiradas de un jugador, de la mas reciente a la mas vieja.</summary>
    public async Task<IReadOnlyList<DiceRound>> GetHistoryAsync(Guid userId, int limit, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        return await session.Query<DiceRound>()
            .Where(round => round.UserId == userId)
            .OrderByDescending(round => round.PlacedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    /// <summary>La Wallet reservo las fichas: se sortea con el nonce ya asignado y se informa el resultado.</summary>
    public async Task OnStakeReservedAsync(StakeReserved message, CancellationToken ct = default)
    {
        await using var session = store.LightweightSession();
        var round = await session.LoadAsync<DiceRound>(message.BetId, ct);
        if (round is null)
        {
            LogForeignBet(logger, message.BetId);
            return;
        }

        if (round.Status != RoundStatus.Placed)
        {
            return; // Entrega duplicada: ya se sorteo.
        }

        var inputs = await fairness.GetDrawInputsAsync(round.UserId, round.Id, ct);
        var outcome = DiceGame.Play(round.ToBet(), inputs.ServerSeed, inputs.ClientSeed, inputs.Nonce);
        round.MarkResolved(outcome.Roll, outcome.Payout);
        session.Store(round);

        // El resultado y el aviso a la Wallet salen en la misma transaccion: no puede quedar sorteada sin avisar.
        await using var outboxSession = outbox.Enroll(session);
        await outboxSession.PublishAsync(new RoundResolved(round.Id, round.AccountId, outcome.Payout));
        await session.SaveChangesAsync(ct);
    }

    public Task OnStakeSettledAsync(StakeSettled message, CancellationToken ct = default) =>
        CloseAsync(message.BetId, (round, now) => round.MarkSettled(now), ct);

    public Task OnStakeRejectedAsync(StakeRejected message, CancellationToken ct = default) =>
        CloseAsync(message.BetId, (round, now) => round.MarkRejected(message.Reason, now), ct);

    public Task OnStakeReleasedAsync(StakeReleased message, CancellationToken ct = default) =>
        CloseAsync(message.BetId, (round, now) => round.MarkVoided("ReservationExpired", now), ct);

    public Task OnSettlementRejectedAsync(StakeSettlementRejected message, CancellationToken ct = default) =>
        CloseAsync(message.BetId, (round, now) => round.MarkVoided(message.Reason, now), ct);

    private async Task CloseAsync(Guid betId, Func<DiceRound, DateTimeOffset, bool> transition, CancellationToken ct)
    {
        await using var session = store.LightweightSession();
        var round = await session.LoadAsync<DiceRound>(betId, ct);
        if (round is null)
        {
            LogForeignBet(logger, betId);
            return;
        }

        if (transition(round, clock.GetUtcNow()))
        {
            session.Store(round);

            // El aviso de "ronda cerrada" sale en la misma transaccion que el cierre y solo cuando la transicion ocurre de verdad.
            await using var outboxSession = outbox.Enroll(session);
            await outboxSession.PublishAsync(new RoundClosed(
                round.Id,
                round.AccountId,
                "Dice",
                round.Status.ToString(),
                round.Roll,
                round.Stake,
                round.Status == RoundStatus.Settled ? round.Payout ?? 0 : 0,
                round.FailureReason));
            await session.SaveChangesAsync(ct);
        }

        // Siempre: si un intento anterior cayo entre guardar la ronda y cerrar la apuesta en el generador, el reintento lo completa (es idempotente).
        if (round.IsClosed)
        {
            await fairness.CompleteBetAsync(round.UserId, round.Id, ct);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Mensaje de una apuesta que no es de dados ({BetId}): se ignora (la atiende otro juego).")]
    private static partial void LogForeignBet(ILogger logger, Guid betId);

    /// <summary>Id de apuesta determinista por (cuenta, clave): reintentar con la misma clave apunta a la misma apuesta.</summary>
    internal static Guid BetIdFor(Guid accountId, string idempotencyKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"bet:{accountId:N}:{idempotencyKey}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
