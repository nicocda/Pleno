using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casino.BuildingBlocks;
using Casino.Contracts;
using Casino.Modules.Games.Crash;
using Casino.Modules.Games.Fairness;
using Casino.Modules.Games.Infrastructure;
using Casino.Modules.Games.Platform;
using JasperFx;
using Marten;
using Marten.Exceptions;
using Microsoft.Extensions.Logging;

namespace Casino.Modules.Games.Application;

/// <summary>El resultado de colocar una apuesta en Crash.</summary>
public sealed record CrashBetPlaced(Guid BetId, Guid RoundId, RoundStatus Status, string Commitment, bool AlreadyPlaced);

/// <summary>El retiro de una apuesta: en que multiplicador (centesimas) y cuanto se cobra.</summary>
public sealed record CrashCashOut(Guid BetId, long Multiplier, long Payout);

/// <summary>Lo que ve un jugador al abrir Crash: la ronda en curso, su apuesta y las ultimas explosiones.</summary>
public sealed record CrashState(
    DateTimeOffset ServerNow,
    double GrowthPerSecond,
    long MinStake,
    long MaxStake,
    CrashRound? Round,
    CrashBet? MyBet,
    IReadOnlyList<CrashRound> History);

/// <summary>
/// El juego Crash. Una ronda COMPARTIDA: se abre la ventana de apuestas, el multiplicador sube y el servidor decide cuando explota; el jugador
/// retira antes de que explote o pierde. Sigue el protocolo de rondas con la Wallet (reservar → resolver → liquidar → cerrar) pero resuelve
/// cada apuesta en cuanto retira (o explota la ronda). El resultado sale de una semilla comprometida ANTES de apostar (provably fair).
/// </summary>
public sealed partial class CrashService(
    IDocumentStore store,
    IOutboxFactory outbox,
    TimeProvider clock,
    SeedProtector protector,
    CrashOptions options,
    ICrashSeedSource seeds,
    ILogger<CrashService> logger) : IGameRounds
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const int ReservationMarginSeconds = 30;

    public CrashOptions Options => options;

    // ---- Ciclo de la ronda (lo maneja el motor) ----

    /// <summary>Abre una ronda: genera la semilla, publica su compromiso y empieza a aceptar apuestas.</summary>
    public async Task<CrashRound> OpenRoundAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7();
        var seed = seeds.NextSeed(id, options.EdgePermille);
        var round = new CrashRound
        {
            Id = id,
            Phase = CrashPhase.Betting,
            Commitment = ProvablyFair.Commitment(seed),
            EncryptedSeed = protector.Protect(seed, AadFor(id)),
            OpenedAt = now,
            BettingEndsAt = now.AddSeconds(options.BettingSeconds),
            EdgePermille = options.EdgePermille,
            GrowthPerSecond = options.GrowthPerSecond,
        };

        await using var session = store.LightweightSession();
        session.Insert(round);
        await using var outboxSession = outbox.Enroll(session);
        await BroadcastAsync(outboxSession, "roundOpened", new { roundId = id, commitment = round.Commitment, bettingEndsAt = round.BettingEndsAt, serverNow = now }, now);
        await session.SaveChangesAsync(ct);
        return round;
    }

    /// <summary>Termina la ventana de apuestas: el multiplicador empieza a subir.</summary>
    public async Task<CrashRound> StartRoundAsync(Guid roundId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        await using var session = store.LightweightSession();
        var round = await session.LoadAsync<CrashRound>(roundId, ct) ?? throw new GamesDomainException(GamesError.RoundNotFound, $"No existe la ronda {roundId}.");
        if (round.StartRunning(now))
        {
            session.Store(round);
            await using var outboxSession = outbox.Enroll(session);
            await BroadcastAsync(outboxSession, "roundStarted", new { roundId, startedAt = now, growthPerSecond = round.GrowthPerSecond, serverNow = now }, now);
            await session.SaveChangesAsync(ct);
        }

        return round;
    }

    /// <summary>El punto de explosion de una ronda (se calcula de la semilla cifrada; nunca se expone antes de tiempo).</summary>
    public long CrashPointOf(CrashRound round)
    {
        ArgumentNullException.ThrowIfNull(round);

        return CrashMath.CrashPoint(protector.Unprotect(round.EncryptedSeed, AadFor(round.Id)), round.Id, round.EdgePermille);
    }

    /// <summary>Las apuestas en juego de una ronda con retiro automatico por debajo del punto de explosion, en el orden en que les toca retirar.</summary>
    public async Task<IReadOnlyList<CrashBet>> AutoCashOutsAsync(Guid roundId, long crashPoint, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        var bets = await session.Query<CrashBet>().Where(b => b.RoundId == roundId && b.Reserved && b.Status == RoundStatus.Placed).ToListAsync(ct);
        return [.. bets.Where(b => b.AutoCashOut is { } auto && auto < crashPoint).OrderBy(b => b.AutoCashOut)];
    }

    /// <summary>Retiro automatico de una apuesta en su multiplicador pedido. Si ya retiro a mano (o ya se resolvio), no hace nada.</summary>
    public async Task AutoCashOutAsync(Guid betId, CancellationToken ct = default)
    {
        await using var session = store.LightweightSession();
        var bet = await session.LoadAsync<CrashBet>(betId, ct);
        if (bet?.AutoCashOut is not { } target || !bet.IsActive)
        {
            return;
        }

        await ResolveAsync(session, bet, b => b.CashOut(target, CrashMath.PayoutFor(b.Stake, target)), ct);
    }

    /// <summary>Explota la ronda: revela la semilla, pierden las apuestas que seguian en juego y se avisa a todos.</summary>
    public async Task CrashRoundAsync(Guid roundId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        await using var session = store.LightweightSession();
        var round = await session.LoadAsync<CrashRound>(roundId, ct) ?? throw new GamesDomainException(GamesError.RoundNotFound, $"No existe la ronda {roundId}.");
        var seed = protector.Unprotect(round.EncryptedSeed, AadFor(round.Id));
        var crashPoint = CrashMath.CrashPoint(seed, round.Id, round.EdgePermille);
        if (!round.MarkCrashed(now, crashPoint, seed))
        {
            return; // ya exploto (entrega repetida)
        }

        session.Store(round);
        await using var outboxSession = outbox.Enroll(session);
        await BroadcastAsync(outboxSession, "roundCrashed", new { roundId, crashPoint, seed, at = now }, now);
        await session.SaveChangesAsync(ct);

        // Cada apuesta en su propia transaccion: si una justo retiro, esa no se pierde y las demas siguen.
        await using var query = store.QuerySession();
        var active = await query.Query<CrashBet>().Where(b => b.RoundId == roundId && b.Reserved && b.Status == RoundStatus.Placed).ToListAsync(ct);
        foreach (var bet in active)
        {
            await using var betSession = store.LightweightSession();
            var fresh = await betSession.LoadAsync<CrashBet>(bet.Id, ct);
            if (fresh is { IsActive: true })
            {
                await ResolveAsync(betSession, fresh, b => b.Lose(), ct);
            }
        }
    }

    /// <summary>Tras un reinicio: toda ronda que quedo a medias se corta y se devuelve lo apostado (nadie pierde por una caida del servicio).</summary>
    public async Task RecoverAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        await using var query = store.QuerySession();
        var open = await query.Query<CrashRound>().Where(r => r.Phase == CrashPhase.Betting || r.Phase == CrashPhase.Running).ToListAsync(ct);
        foreach (var round in open)
        {
            await using var session = store.LightweightSession();
            var fresh = await session.LoadAsync<CrashRound>(round.Id, ct);
            if (fresh is null || !fresh.MarkAborted(now))
            {
                continue;
            }

            session.Store(fresh);
            await using var outboxSession = outbox.Enroll(session);
            await BroadcastAsync(outboxSession, "roundAborted", new { roundId = round.Id, at = now }, now);
            await session.SaveChangesAsync(ct);

            await using var betQuery = store.QuerySession();
            var bets = await betQuery.Query<CrashBet>().Where(b => b.RoundId == round.Id && b.Status == RoundStatus.Placed).ToListAsync(ct);
            foreach (var bet in bets.Where(b => b.Reserved))
            {
                await using var betSession = store.LightweightSession();
                var current = await betSession.LoadAsync<CrashBet>(bet.Id, ct);
                if (current is { Status: RoundStatus.Placed, Reserved: true })
                {
                    await ResolveAsync(betSession, current, b => b.Refund("RoundAborted"), ct);
                }
            }
        }
    }

    // ---- Lo que hace el jugador ----

    public async Task<CrashBetPlaced> PlaceBetAsync(Guid userId, Guid accountId, long stake, long? autoCashOut, string idempotencyKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
        {
            throw new GamesDomainException(GamesError.InvalidIdempotencyKey, "La IdempotencyKey es obligatoria y de hasta 128 caracteres.");
        }

        if (stake < options.MinStake || stake > options.MaxStake)
        {
            throw new GamesDomainException(GamesError.InvalidBet, $"La apuesta va de {options.MinStake} a {options.MaxStake} fichas.");
        }

        if (autoCashOut is { } auto && (auto <= CrashMath.OneX || auto > CrashMath.MaxMultiplier))
        {
            throw new GamesDomainException(GamesError.InvalidBet, "El retiro automatico va de x1,01 a x100,00.");
        }

        var betId = BetIdFor(accountId, idempotencyKey);
        await using var session = store.LightweightSession();

        // Reintento con la misma clave: devuelve la misma apuesta (y se rechaza si cambia el contenido).
        var existing = await session.LoadAsync<CrashBet>(betId, ct);
        if (existing is not null)
        {
            return await ExistingAsync(session, existing, accountId, stake, autoCashOut, ct);
        }

        var now = clock.GetUtcNow();
        var round = FirstOrNull(await session.Query<CrashRound>().Where(r => r.Phase == CrashPhase.Betting).OrderByDescending(r => r.OpenedAt).Take(1).ToListAsync(ct));
        if (round is null || now >= round.BettingEndsAt)
        {
            throw new GamesDomainException(GamesError.BettingClosed, "No hay una ronda abierta para apostar. Espera a la siguiente.");
        }

        var bet = new CrashBet
        {
            Id = betId,
            RoundId = round.Id,
            UserId = userId,
            AccountId = accountId,
            Stake = stake,
            AutoCashOut = autoCashOut,
            Status = RoundStatus.Placed,
            PlacedAt = now,
        };

        try
        {
            session.Insert(bet);
            await using var outboxSession = outbox.Enroll(session);
            await outboxSession.PublishAsync(new ReserveStake(betId, accountId, stake, ReservationSecondsFor(round, now), "crash"));
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is DocumentAlreadyExistsException || ex.InnerException is DocumentAlreadyExistsException)
        {
            // Dos pedidos iguales a la vez: gano el primero.
            await using var other = store.LightweightSession();
            var winner = await other.LoadAsync<CrashBet>(betId, ct);
            if (winner is null)
            {
                throw;
            }

            return await ExistingAsync(other, winner, accountId, stake, autoCashOut, ct);
        }

        return new CrashBetPlaced(betId, round.Id, RoundStatus.Placed, round.Commitment, AlreadyPlaced: false);
    }

    /// <summary>
    /// Retira una apuesta en el multiplicador actual. El servidor decide el multiplicador por el reloj (no el cliente): si el retiro llega cuando
    /// ya habia explotado, se rechaza y la apuesta se pierde.
    /// </summary>
    public async Task<CrashCashOut> CashOutAsync(Guid userId, Guid betId, CancellationToken ct = default)
    {
        await using var session = store.LightweightSession();
        var bet = await session.LoadAsync<CrashBet>(betId, ct);
        if (bet is null || bet.UserId != userId)
        {
            throw new GamesDomainException(GamesError.RoundNotFound, $"No existe la apuesta {betId}.");
        }

        if (!bet.IsActive)
        {
            throw new GamesDomainException(GamesError.BetNotActive, "Esa apuesta ya no esta en juego.");
        }

        var round = await session.LoadAsync<CrashRound>(bet.RoundId, ct);
        if (round is not { Phase: CrashPhase.Running, StartedAt: { } startedAt })
        {
            throw new GamesDomainException(GamesError.RoundNotRunning, "La ronda todavia no empezo a subir o ya termino.");
        }

        var multiplier = CrashMath.MultiplierAt((clock.GetUtcNow() - startedAt).TotalSeconds, round.GrowthPerSecond);
        if (multiplier >= CrashPointOf(round))
        {
            throw new GamesDomainException(GamesError.CrashedAlready, "El cohete exploto antes de que llegara tu retiro.");
        }

        try
        {
            await ResolveAsync(session, bet, b => b.CashOut(multiplier, CrashMath.PayoutFor(b.Stake, multiplier)), ct);
        }
        catch (ConcurrencyException)
        {
            // En el mismo instante el motor la dio por perdida (exploto): gano esa decision.
            throw new GamesDomainException(GamesError.CrashedAlready, "El cohete exploto antes de que llegara tu retiro.");
        }

        return new CrashCashOut(betId, multiplier, bet.Payout!.Value);
    }

    public async Task<CrashState> GetStateAsync(Guid userId, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        var round = FirstOrNull(await session.Query<CrashRound>().OrderByDescending(r => r.OpenedAt).Take(1).ToListAsync(ct));
        CrashBet? mine = null;
        if (round is not null)
        {
            mine = FirstOrNull(await session.Query<CrashBet>().Where(b => b.RoundId == round.Id && b.UserId == userId).OrderByDescending(b => b.PlacedAt).Take(1).ToListAsync(ct));
        }

        var history = await session.Query<CrashRound>().Where(r => r.Phase == CrashPhase.Crashed).OrderByDescending(r => r.OpenedAt).Take(20).ToListAsync(ct);
        return new CrashState(clock.GetUtcNow(), options.GrowthPerSecond, options.MinStake, options.MaxStake, round, mine, history);
    }

    public async Task<CrashRound> GetRoundAsync(Guid roundId, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        return await session.LoadAsync<CrashRound>(roundId, ct) ?? throw new GamesDomainException(GamesError.RoundNotFound, $"No existe la ronda {roundId}.");
    }

    public async Task<IReadOnlyList<CrashRound>> GetRecentRoundsAsync(int limit, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        return await session.Query<CrashRound>().Where(r => r.Phase == CrashPhase.Crashed).OrderByDescending(r => r.OpenedAt).Take(limit).ToListAsync(ct);
    }

    public async Task<CrashBet> GetBetAsync(Guid betId, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        return await session.LoadAsync<CrashBet>(betId, ct) ?? throw new GamesDomainException(GamesError.RoundNotFound, $"No existe la apuesta {betId}.");
    }

    public async Task<IReadOnlyList<CrashBet>> GetHistoryAsync(Guid userId, int limit, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        return await session.Query<CrashBet>().Where(b => b.UserId == userId).OrderByDescending(b => b.PlacedAt).Take(limit).ToListAsync(ct);
    }

    // ---- Protocolo de rondas con la Wallet ----

    /// <summary>La Wallet reservo las fichas: la apuesta pasa a estar en juego. Si llego tarde (la ronda ya no acepta apuestas), se devuelve lo apostado.</summary>
    public async Task OnStakeReservedAsync(StakeReserved message, CancellationToken ct = default)
    {
        await using var session = store.LightweightSession();
        var bet = await session.LoadAsync<CrashBet>(message.BetId, ct);
        if (bet is null || bet.Status != RoundStatus.Placed || bet.Reserved)
        {
            return; // no es de Crash, o es una entrega repetida
        }

        var round = await session.LoadAsync<CrashRound>(bet.RoundId, ct);
        if (round is { Phase: CrashPhase.Betting })
        {
            bet.MarkReserved();
            session.Store(bet);
            await session.SaveChangesAsync(ct);
            return;
        }

        await ResolveAsync(session, bet, b => b.Refund("BettingClosed"), ct);
    }

    public Task OnStakeSettledAsync(StakeSettled message, CancellationToken ct = default) =>
        CloseAsync(message.BetId, (bet, now) => bet.MarkSettled(now), ct);

    public Task OnStakeRejectedAsync(StakeRejected message, CancellationToken ct = default) =>
        CloseAsync(message.BetId, (bet, now) => bet.MarkRejected(message.Reason, now), ct);

    public Task OnStakeReleasedAsync(StakeReleased message, CancellationToken ct = default) =>
        CloseAsync(message.BetId, (bet, now) => bet.MarkVoided("ReservationExpired", now), ct);

    public Task OnSettlementRejectedAsync(StakeSettlementRejected message, CancellationToken ct = default) =>
        CloseAsync(message.BetId, (bet, now) => bet.MarkVoided(message.Reason, now), ct);

    // ---- Interno ----

    /// <summary>Aplica una transicion y, en la MISMA transaccion, avisa a la Wallet el premio total para que liquide.</summary>
    private async Task ResolveAsync(IDocumentSession session, CrashBet bet, Func<CrashBet, bool> transition, CancellationToken ct)
    {
        if (!transition(bet))
        {
            return;
        }

        session.Store(bet);
        await using var outboxSession = outbox.Enroll(session);
        await outboxSession.PublishAsync(new RoundResolved(bet.Id, bet.AccountId, bet.Payout ?? 0));
        await session.SaveChangesAsync(ct);
    }

    private async Task CloseAsync(Guid betId, Func<CrashBet, DateTimeOffset, bool> transition, CancellationToken ct)
    {
        await using var session = store.LightweightSession();
        var bet = await session.LoadAsync<CrashBet>(betId, ct);
        if (bet is null)
        {
            return; // no es de Crash
        }

        if (transition(bet, clock.GetUtcNow()))
        {
            session.Store(bet);
            await using var outboxSession = outbox.Enroll(session);
            await outboxSession.PublishAsync(new RoundClosed(
                bet.Id,
                bet.AccountId,
                "Crash",
                bet.Status.ToString(),
                WinningNumber: null,
                bet.Stake,
                bet.Status == RoundStatus.Settled ? bet.Payout ?? 0 : 0,
                bet.FailureReason));
            await session.SaveChangesAsync(ct);
            LogClosed(logger, bet.Id, bet.Status);
        }
    }

    private static async Task<CrashBetPlaced> ExistingAsync(IDocumentSession session, CrashBet existing, Guid accountId, long stake, long? autoCashOut, CancellationToken ct)
    {
        if (existing.AccountId != accountId || existing.Stake != stake || existing.AutoCashOut != autoCashOut)
        {
            throw new GamesDomainException(GamesError.BetKeyReused, "La IdempotencyKey ya se uso con otra apuesta.");
        }

        var round = await session.LoadAsync<CrashRound>(existing.RoundId, ct);
        return new CrashBetPlaced(existing.Id, existing.RoundId, existing.Status, round?.Commitment ?? string.Empty, AlreadyPlaced: true);
    }

    /// <summary>La reserva tiene que durar hasta el final de la ronda aunque suba hasta el tope: lo que falta para apostar + lo que puede tardar en subir + un margen.</summary>
    private static int ReservationSecondsFor(CrashRound round, DateTimeOffset now)
    {
        var untilStart = Math.Max(0, (round.BettingEndsAt - now).TotalSeconds);
        var longestRun = CrashMath.TimeToReach(CrashMath.MaxMultiplier, round.GrowthPerSecond).TotalSeconds;
        return (int)Math.Ceiling(untilStart + longestRun + ReservationMarginSeconds);
    }

    private static T? FirstOrNull<T>(IReadOnlyList<T> items)
        where T : class => items.Count > 0 ? items[0] : null;

    private static string AadFor(Guid roundId) => $"crash:{roundId:N}";

    private static ValueTask BroadcastAsync(IOutboxSession outboxSession, string kind, object data, DateTimeOffset at) =>
        outboxSession.PublishAsync(new GameBroadcast("crash", kind, JsonSerializer.Serialize(data, Json), at));

    /// <summary>Id de apuesta determinista por (cuenta, clave): reintentar con la misma clave apunta a la misma apuesta.</summary>
    internal static Guid BetIdFor(Guid accountId, string idempotencyKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"crash:{accountId:N}:{idempotencyKey}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Apuesta de Crash {BetId} cerrada: {Status}.")]
    private static partial void LogClosed(ILogger logger, Guid betId, RoundStatus status);
}
