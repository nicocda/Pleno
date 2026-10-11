using System.Security.Cryptography;
using System.Text;
using Casino.BuildingBlocks;
using Casino.Contracts;
using Casino.Modules.Games.Platform;
using Casino.Modules.Games.Roulette;
using Marten;
using Microsoft.Extensions.Logging;

namespace Casino.Modules.Games.Application;

public sealed record PlaceRouletteBetRequest(Guid UserId, Guid AccountId, RouletteBetType BetType, IReadOnlyList<int> Selection, long Stake);

/// <summary>Una apuesta dentro de una tirada.</summary>
public sealed record BetLine(RouletteBetType BetType, IReadOnlyList<int> Selection, long Stake);

/// <summary>Una tirada con una o varias apuestas: comparten nonce, numero sorteado y reserva.</summary>
public sealed record PlaceRouletteSpinRequest(Guid UserId, Guid AccountId, IReadOnlyList<BetLine> Bets);

/// <summary>
/// Coordina la vida de una apuesta de ruleta. No hay un orquestador: cada paso reacciona a un mensaje de la Wallet y es idempotente.
/// Colocar -> (Wallet reserva) -> sortear -> (Wallet liquida) -> cerrar; si la Wallet rechaza o vence la reserva, se cierra anulada.
/// </summary>
public sealed partial class RouletteService(
    IDocumentStore store,
    FairnessService fairness,
    IOutboxFactory outbox,
    TimeProvider clock,
    ILogger<RouletteService> logger) : IGameRounds
{
    /// <summary>Maximo de apuestas en una tirada: alcanza para cubrir todo el tapete (157 lugares) y acota el tamaño de la ronda.</summary>
    public const int MaxBetsPerSpin = 200;

    /// <summary>Coloca una apuesta suelta (una tirada de una sola apuesta).</summary>
    public Task<PlacedBet> PlaceBetAsync(PlaceRouletteBetRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PlaceSpinAsync(
            new PlaceRouletteSpinRequest(request.UserId, request.AccountId, [new BetLine(request.BetType, request.Selection, request.Stake)]),
            idempotencyKey,
            ct);
    }

    /// <summary>
    /// Coloca una tirada. En UNA transaccion: asigna el nonce (lo decide el servidor), guarda la ronda y encola la orden
    /// de reserva a la Wallet por el TOTAL apostado. Es idempotente: la misma IdempotencyKey de la misma cuenta devuelve la misma tirada.
    /// </summary>
    public async Task<PlacedBet> PlaceSpinAsync(PlaceRouletteSpinRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
        {
            throw new GamesDomainException(GamesError.InvalidIdempotencyKey, "La IdempotencyKey es obligatoria y de hasta 128 caracteres.");
        }

        if (request.Bets is null || request.Bets.Count is 0 or > MaxBetsPerSpin)
        {
            throw new GamesDomainException(GamesError.InvalidBet, $"Una tirada lleva entre 1 y {MaxBetsPerSpin} apuestas.");
        }

        long total = 0;
        foreach (var line in request.Bets)
        {
            if (!RouletteBet.TryCreate(line.BetType, line.Selection, line.Stake, out _))
            {
                throw new GamesDomainException(GamesError.InvalidBet, $"Apuesta invalida: {line.BetType} con esa seleccion y apuesta {line.Stake}.");
            }

            try
            {
                total = checked(total + line.Stake);
            }
            catch (OverflowException)
            {
                throw new GamesDomainException(GamesError.InvalidBet, "El total apostado es demasiado grande.");
            }
        }

        var betId = BetIdFor(request.AccountId, idempotencyKey);
        var now = clock.GetUtcNow();
        var bets = request.Bets.Select(line => new RoundBet { BetType = line.BetType, Selection = [.. line.Selection], Stake = line.Stake }).ToList();

        var allocation = await fairness.AllocateNonceAsync(
            request.UserId,
            betId,
            async (session, outboxSession, nonce) =>
            {
                session.Insert(new RouletteRound
                {
                    Id = betId,
                    UserId = request.UserId,
                    AccountId = request.AccountId,
                    BetType = bets[0].BetType,
                    Selection = [.. bets[0].Selection],
                    Stake = total,
                    Bets = bets,
                    PairId = nonce.PairId,
                    Nonce = nonce.Nonce,
                    Status = RoundStatus.Placed,
                    PlacedAt = now,
                });

                await outboxSession.PublishAsync(new ReserveStake(betId, request.AccountId, total, GameId: "roulette"));
            },
            ct);

        if (allocation.AlreadyAllocated)
        {
            // Reintento: la ronda ya existe. Si la misma clave llega con otro contenido, es un error del cliente.
            var existing = await GetRoundAsync(betId, ct);
            if (existing.AccountId != request.AccountId
                || existing.UserId != request.UserId
                || existing.Stake != total
                || !Canonical(existing.AllBets().Select(b => (b.BetType, (IReadOnlyList<int>)b.Selection, b.Stake)))
                    .SequenceEqual(Canonical(request.Bets.Select(b => (b.BetType, b.Selection, b.Stake)))))
            {
                throw new GamesDomainException(GamesError.BetKeyReused, "La IdempotencyKey ya se uso con otra apuesta.");
            }

            return new PlacedBet(betId, existing.Status, allocation.Nonce, allocation.Commitment, allocation.ClientSeed, AlreadyPlaced: true);
        }

        return new PlacedBet(betId, RoundStatus.Placed, allocation.Nonce, allocation.Commitment, allocation.ClientSeed, AlreadyPlaced: false);
    }

    /// <summary>Forma comparable de una lista de apuestas: no depende del orden ni del orden de la seleccion.</summary>
    private static IEnumerable<string> Canonical(IEnumerable<(RouletteBetType Type, IReadOnlyList<int> Selection, long Stake)> bets) =>
        bets.Select(b => $"{b.Type}:{string.Join('-', b.Selection.Order())}:{b.Stake}").Order(StringComparer.Ordinal);

    public async Task<RouletteRound> GetRoundAsync(Guid betId, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        return await session.LoadAsync<RouletteRound>(betId, ct)
            ?? throw new GamesDomainException(GamesError.RoundNotFound, $"No existe la ronda {betId}.");
    }

    /// <summary>Ultimas rondas de un jugador, de la mas reciente a la mas vieja.</summary>
    public async Task<IReadOnlyList<RouletteRound>> GetHistoryAsync(Guid userId, int limit, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        return await session.Query<RouletteRound>()
            .Where(round => round.UserId == userId)
            .OrderByDescending(round => round.PlacedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    /// <summary>La Wallet reservo las fichas: se sortea con el nonce ya asignado y se informa el resultado.</summary>
    public async Task OnStakeReservedAsync(StakeReserved message, CancellationToken ct = default)
    {
        await using var session = store.LightweightSession();
        var round = await session.LoadAsync<RouletteRound>(message.BetId, ct);
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
        var outcome = RouletteGame.PlayMany(round.ToBets(), inputs.ServerSeed, inputs.ClientSeed, inputs.Nonce);
        round.MarkResolved(outcome.WinningNumber, outcome.Payout);
        session.Store(round);

        // El resultado y el aviso a la Wallet salen en la misma transaccion: no puede quedar sorteada sin avisar.
        await using var outboxSession = outbox.Enroll(session);
        await outboxSession.PublishAsync(new RoundResolved(round.Id, round.AccountId, outcome.Payout));
        await session.SaveChangesAsync(ct);
    }

    /// <summary>La Wallet liquido el premio: la ronda termina bien.</summary>
    public Task OnStakeSettledAsync(StakeSettled message, CancellationToken ct = default) =>
        CloseAsync(message.BetId, (round, now) => round.MarkSettled(now), ct);

    /// <summary>La Wallet no pudo reservar (saldo insuficiente, cuenta inexistente...): la apuesta nunca se jugo.</summary>
    public Task OnStakeRejectedAsync(StakeRejected message, CancellationToken ct = default) =>
        CloseAsync(message.BetId, (round, now) => round.MarkRejected(message.Reason, now), ct);

    /// <summary>Vencio la reserva y la Wallet devolvio las fichas: la ronda se anula.</summary>
    public Task OnStakeReleasedAsync(StakeReleased message, CancellationToken ct = default) =>
        CloseAsync(message.BetId, (round, now) => round.MarkVoided("ReservationExpired", now), ct);

    /// <summary>La Wallet no pudo liquidar (el resultado llego tarde): la ronda se anula.</summary>
    public Task OnSettlementRejectedAsync(StakeSettlementRejected message, CancellationToken ct = default) =>
        CloseAsync(message.BetId, (round, now) => round.MarkVoided(message.Reason, now), ct);

    private async Task CloseAsync(Guid betId, Func<RouletteRound, DateTimeOffset, bool> transition, CancellationToken ct)
    {
        await using var session = store.LightweightSession();
        var round = await session.LoadAsync<RouletteRound>(betId, ct);
        if (round is null)
        {
            LogForeignBet(logger, betId);
            return;
        }

        if (transition(round, clock.GetUtcNow()))
        {
            session.Store(round);

            // El aviso de "ronda cerrada" sale en la misma transaccion que el cierre, y solo cuando la transicion
            // realmente ocurre: una entrega duplicada no vuelve a avisar.
            await using var outboxSession = outbox.Enroll(session);
            await outboxSession.PublishAsync(new RoundClosed(
                round.Id,
                round.AccountId,
                "Roulette",
                round.Status.ToString(),
                round.WinningNumber,
                round.Stake,
                round.Status == RoundStatus.Settled ? round.Payout ?? 0 : 0,
                round.FailureReason));
            await session.SaveChangesAsync(ct);
        }

        // Siempre (aunque la transicion ya estuviera aplicada): si un intento anterior cayo entre guardar la ronda y
        // cerrar la apuesta en el generador, el reintento lo completa. CompleteBet es idempotente.
        if (round.IsClosed)
        {
            await fairness.CompleteBetAsync(round.UserId, round.Id, ct);
        }
    }
    [LoggerMessage(Level = LogLevel.Debug, Message = "Mensaje de una apuesta que no es de ruleta ({BetId}): se ignora (la atiende otro juego).")]
    private static partial void LogForeignBet(ILogger logger, Guid betId);


    /// <summary>Id de apuesta determinista por (cuenta, clave): reintentar con la misma clave apunta a la misma apuesta.</summary>
    internal static Guid BetIdFor(Guid accountId, string idempotencyKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"bet:{accountId:N}:{idempotencyKey}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
