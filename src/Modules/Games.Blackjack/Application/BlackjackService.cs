using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casino.BuildingBlocks;
using Casino.Contracts;
using Casino.Modules.Games.Blackjack;
using Casino.Modules.Games.Fairness;
using Casino.Modules.Games.Infrastructure;
using Casino.Modules.Games.Platform;
using JasperFx;
using Marten;
using Marten.Exceptions;
using Microsoft.Extensions.Logging;

namespace Casino.Modules.Games.Application;

/// <summary>El resultado de apostar (sentarse) en una mano de Blackjack.</summary>
public sealed record BlackjackBetPlaced(Guid BetId, Guid RoundId, string TableId, RoundStatus Status, string Commitment, bool AlreadyPlaced);

/// <summary>Una mesa en el listado: su configuracion y como esta la mano actual.</summary>
public sealed record BlackjackTableSummary(BlackjackTableConfig Table, BlackjackPhase? Phase, int Players);

/// <summary>Lo que ve un jugador al sentarse a mirar una mesa: la mano en curso y las apuestas de todos los asientos (la mano de cada uno es publica en la mesa).</summary>
public sealed record BlackjackTableState(DateTimeOffset ServerNow, BlackjackTableConfig Table, BlackjackOptions Options, BlackjackRound? Round, IReadOnlyList<BlackjackBet> Bets);

/// <summary>
/// El juego Blackjack en mesas fijas, contra el crupier y con varios jugadores a la vez. Una mano COMPARTIDA por mesa: se abre la ventana de apuestas
/// (el reloj arranca con la primera apuesta), se reparte, juega cada jugador por turno (pedir o plantarse), juega el crupier y se paga. Sigue el
/// protocolo de rondas con la Wallet por cada apuesta (reservar → resolver → liquidar → cerrar). El mazo sale de una semilla comprometida ANTES de
/// apostar (provably fair). Reglas: crupier planta en todos los 17, blackjack paga 3 a 2; sin doblar, dividir ni seguro (por ahora).
/// </summary>
public sealed partial class BlackjackService(
    IDocumentStore store,
    IOutboxFactory outbox,
    TimeProvider clock,
    SeedProtector protector,
    BlackjackOptions options,
    IBlackjackSeedSource seeds,
    ILogger<BlackjackService> logger) : IGameRounds
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const int ReservationMarginSeconds = 30;
    private const int ConflictRetries = 4;

    public BlackjackOptions Options => options;

    // ---- Ciclo de la mano (lo maneja el motor) ----

    /// <summary>Abre una mano en la mesa: genera la semilla, publica su compromiso y queda esperando la primera apuesta.</summary>
    public async Task<BlackjackRound> OpenRoundAsync(string tableId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7();
        var seed = seeds.NextSeed(id);
        var round = new BlackjackRound
        {
            Id = id,
            TableId = tableId,
            Phase = BlackjackPhase.Betting,
            Commitment = ProvablyFair.Commitment(seed),
            EncryptedSeed = protector.Protect(seed, AadFor(id)),
            OpenedAt = now,
        };

        await using var session = store.LightweightSession();
        session.Insert(round);
        await using var outboxSession = outbox.Enroll(session);
        await BroadcastAsync(outboxSession, round, now);
        await session.SaveChangesAsync(ct);
        return round;
    }

    public async Task<BlackjackRound> GetRoundAsync(Guid roundId, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        return await session.LoadAsync<BlackjackRound>(roundId, ct) ?? throw new GamesDomainException(GamesError.RoundNotFound, $"No existe la mano {roundId}.");
    }

    /// <summary>Cuantas apuestas reservadas tiene la mano (lo que el motor necesita saber al cerrar la ventana de apuestas).</summary>
    public async Task<int> ReservedBetsAsync(Guid roundId, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        return await session.Query<BlackjackBet>().CountAsync(b => b.RoundId == roundId && b.Status == RoundStatus.Placed && b.Reserved, ct);
    }

    /// <summary>
    /// Red de seguridad: una mano que espera la primera apuesta (sin reloj) pero ya tiene una apuesta reservada arranca la ventana. Pasa si la reserva llego tarde
    /// (por ejemplo con el sistema recien levantado) y la mano ya habia vuelto a esperar. Devuelve si arranco el reloj.
    /// </summary>
    public async Task<bool> RearmClockAsync(Guid roundId, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var session = store.LightweightSession();
                var round = await session.LoadAsync<BlackjackRound>(roundId, ct);
                if (round is not { Phase: BlackjackPhase.Betting, BettingEndsAt: null })
                {
                    return false;
                }

                var reserved = await session.Query<BlackjackBet>().CountAsync(b => b.RoundId == roundId && b.Status == RoundStatus.Placed && b.Reserved, ct);
                if (reserved == 0 || !round.StartBettingClock(clock.GetUtcNow(), options.BettingSeconds))
                {
                    return false;
                }

                session.Store(round);
                await using var outboxSession = outbox.Enroll(session);
                await BroadcastAsync(outboxSession, round, clock.GetUtcNow());
                await session.SaveChangesAsync(ct);
                return true;
            }
            catch (ConcurrencyException) when (attempt < ConflictRetries)
            {
                // Alguien toco la mano a la vez: se vuelve a leer.
            }
        }
    }

    /// <summary>
    /// Cierra las apuestas y reparte. Si nadie llego a reservar, la mano vuelve a esperar (devuelve null). Los jugadores se sientan en el orden en que apostaron;
    /// si hay mas que asientos, los que sobran recuperan lo apostado. Si el crupier tiene blackjack natural se muestra y la mano se resuelve sin turnos.
    /// </summary>
    public async Task<IReadOnlyList<BlackjackBet>?> DealAsync(Guid roundId, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await TryDealAsync(roundId, ct);
            }
            catch (ConcurrencyException) when (attempt < ConflictRetries)
            {
                // Una reserva llego justo ahora y toco la mano: se vuelve a leer todo y se reparte con lo que haya.
            }
        }
    }

    private async Task<IReadOnlyList<BlackjackBet>?> TryDealAsync(Guid roundId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var session = store.LightweightSession();
        var round = await session.LoadAsync<BlackjackRound>(roundId, ct) ?? throw new GamesDomainException(GamesError.RoundNotFound, $"No existe la mano {roundId}.");
        var reserved = await session.Query<BlackjackBet>().Where(b => b.RoundId == roundId && b.Status == RoundStatus.Placed && b.Reserved).OrderBy(b => b.PlacedAt).ToListAsync(ct);
        if (round.Phase != BlackjackPhase.Betting)
        {
            return [.. reserved.Where(b => b.Seat > 0).OrderBy(b => b.Seat)];
        }

        if (reserved.Count == 0)
        {
            if (round.BackToWaiting())
            {
                session.Store(round);
                await session.SaveChangesAsync(ct);
            }

            return null;
        }

        var seated = reserved.Take(options.MaxSeats).ToList();
        var seats = seated.Count;
        var shoe = BlackjackMath.Shoe(protector.Unprotect(round.EncryptedSeed, AadFor(round.Id)), round.Id);
        for (var i = 0; i < seats; i++)
        {
            seated[i].Deal(i + 1, shoe[BlackjackMath.PlayerFirstCard(i)], shoe[BlackjackMath.PlayerSecondCard(seats, i)]);
            session.Store(seated[i]);
        }

        round.BeginPlaying(seats, shoe[BlackjackMath.DealerUpCard(seats)], BlackjackMath.FirstFreeCard(seats));
        var hole = shoe[BlackjackMath.DealerHoleCard(seats)];
        if (BlackjackMath.IsBlackjack([round.DealerCards[0], hole]))
        {
            // El crupier tiene blackjack: se muestra ya y nadie juega (los que tengan blackjack empatan, los demas pierden).
            round.RevealHole(hole);
            foreach (var bet in seated)
            {
                bet.Stand();
            }
        }

        session.Store(round);
        await using var outboxSession = outbox.Enroll(session);
        await BroadcastAsync(outboxSession, round, now);
        await session.SaveChangesAsync(ct);

        // Los que reservaron pero no entraron en la mesa (llena) recuperan lo apostado.
        foreach (var extra in reserved.Skip(options.MaxSeats))
        {
            await using var extraSession = store.LightweightSession();
            var fresh = await extraSession.LoadAsync<BlackjackBet>(extra.Id, ct);
            if (fresh is not null)
            {
                await ResolveAsync(extraSession, fresh, b => b.Refund("TableFull"), ct);
            }
        }

        return seated;
    }

    /// <summary>Le da el turno a una apuesta: tiene hasta <c>TurnSeconds</c> para pedir o plantarse.</summary>
    public async Task<DateTimeOffset> StartTurnAsync(Guid roundId, Guid betId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var endsAt = now.AddSeconds(options.TurnSeconds);
        await using var session = store.LightweightSession();
        var round = await session.LoadAsync<BlackjackRound>(roundId, ct) ?? throw new GamesDomainException(GamesError.RoundNotFound, $"No existe la mano {roundId}.");
        if (round.SetTurn(betId, endsAt))
        {
            session.Store(round);
            await using var outboxSession = outbox.Enroll(session);
            await BroadcastAsync(outboxSession, round, now);
            await session.SaveChangesAsync(ct);
        }

        return endsAt;
    }

    public async Task<BlackjackBet> GetBetAsync(Guid betId, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        return await session.LoadAsync<BlackjackBet>(betId, ct) ?? throw new GamesDomainException(GamesError.RoundNotFound, $"No existe la apuesta {betId}.");
    }

    /// <summary>Se acabo el tiempo del turno sin que el jugador actuara: se planta. Si justo actuo, no hace nada.</summary>
    public async Task AutoStandAsync(Guid betId, CancellationToken ct = default)
    {
        await using var session = store.LightweightSession();
        var bet = await session.LoadAsync<BlackjackBet>(betId, ct);
        if (bet is null || !bet.Stand())
        {
            return;
        }

        try
        {
            session.Store(bet);
            await session.SaveChangesAsync(ct);
        }
        catch (ConcurrencyException)
        {
            // En el mismo instante el jugador pidio o se planto: gano esa decision.
        }
    }

    /// <summary>El crupier da vuelta su carta tapada (la calcula del zapato recien ahora).</summary>
    public async Task<BlackjackRound> RevealHoleAsync(Guid roundId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        await using var session = store.LightweightSession();
        var round = await session.LoadAsync<BlackjackRound>(roundId, ct) ?? throw new GamesDomainException(GamesError.RoundNotFound, $"No existe la mano {roundId}.");
        if (round.Phase == BlackjackPhase.Playing && !round.DealerRevealed)
        {
            var shoe = BlackjackMath.Shoe(protector.Unprotect(round.EncryptedSeed, AadFor(round.Id)), round.Id);
            round.RevealHole(shoe[BlackjackMath.DealerHoleCard(round.SeatCount)]);
            session.Store(round);
            await using var outboxSession = outbox.Enroll(session);
            await BroadcastAsync(outboxSession, round, now);
            await session.SaveChangesAsync(ct);
        }

        return round;
    }

    /// <summary>El crupier pide una carta.</summary>
    public async Task<BlackjackRound> DealerDrawAsync(Guid roundId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        await using var session = store.LightweightSession();
        var round = await session.LoadAsync<BlackjackRound>(roundId, ct) ?? throw new GamesDomainException(GamesError.RoundNotFound, $"No existe la mano {roundId}.");
        if (round is { Phase: BlackjackPhase.Playing, DealerRevealed: true } && BlackjackMath.DealerMustDraw(round.DealerCards))
        {
            var shoe = BlackjackMath.Shoe(protector.Unprotect(round.EncryptedSeed, AadFor(round.Id)), round.Id);
            round.DealerDraws(shoe[round.NextCard]);
            session.Store(round);
            await using var outboxSession = outbox.Enroll(session);
            await BroadcastAsync(outboxSession, round, now);
            await session.SaveChangesAsync(ct);
        }

        return round;
    }

    /// <summary>Resuelve cada apuesta contra el crupier (cada una en su transaccion), revela la semilla y cierra la mano.</summary>
    public async Task SettleAsync(Guid roundId, CancellationToken ct = default)
    {
        await using var query = store.QuerySession();
        var round = await query.LoadAsync<BlackjackRound>(roundId, ct) ?? throw new GamesDomainException(GamesError.RoundNotFound, $"No existe la mano {roundId}.");
        if (round.Phase != BlackjackPhase.Playing)
        {
            return;
        }

        var bets = await query.Query<BlackjackBet>().Where(b => b.RoundId == roundId && b.Status == RoundStatus.Placed && b.Reserved).ToListAsync(ct);
        foreach (var bet in bets)
        {
            await using var betSession = store.LightweightSession();
            var fresh = await betSession.LoadAsync<BlackjackBet>(bet.Id, ct);
            if (fresh is { IsActive: true })
            {
                await ResolveAsync(betSession, fresh, b => b.Finish(BlackjackMath.Resolve(b.Cards, round.DealerCards)), ct);
            }
        }

        var now = clock.GetUtcNow();
        await using var session = store.LightweightSession();
        var current = await session.LoadAsync<BlackjackRound>(roundId, ct);
        if (current is not null && current.Finish(now, protector.Unprotect(current.EncryptedSeed, AadFor(current.Id))))
        {
            session.Store(current);
            await using var outboxSession = outbox.Enroll(session);
            await BroadcastAsync(outboxSession, current, now);
            await session.SaveChangesAsync(ct);
        }
    }

    /// <summary>Tras un reinicio: toda mano que quedo a medias se corta y se devuelve lo apostado (nadie pierde por una caida del servicio).</summary>
    public async Task RecoverAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        await using var query = store.QuerySession();
        var open = await query.Query<BlackjackRound>().Where(r => r.Phase == BlackjackPhase.Betting || r.Phase == BlackjackPhase.Playing).ToListAsync(ct);
        foreach (var round in open)
        {
            await using var session = store.LightweightSession();
            var fresh = await session.LoadAsync<BlackjackRound>(round.Id, ct);
            if (fresh is null || !fresh.MarkAborted(now))
            {
                continue;
            }

            session.Store(fresh);
            await using var outboxSession = outbox.Enroll(session);
            await BroadcastAsync(outboxSession, fresh, now);
            await session.SaveChangesAsync(ct);

            await using var betQuery = store.QuerySession();
            var bets = await betQuery.Query<BlackjackBet>().Where(b => b.RoundId == round.Id && b.Status == RoundStatus.Placed).ToListAsync(ct);
            foreach (var bet in bets.Where(b => b.Reserved))
            {
                await using var betSession = store.LightweightSession();
                var current = await betSession.LoadAsync<BlackjackBet>(bet.Id, ct);
                if (current is { Status: RoundStatus.Placed, Reserved: true })
                {
                    await ResolveAsync(betSession, current, b => b.Refund("RoundAborted"), ct);
                }
            }
        }
    }

    // ---- Lo que hace el jugador ----

    public async Task<BlackjackBetPlaced> PlaceBetAsync(Guid userId, Guid accountId, string tableId, long stake, string idempotencyKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
        {
            throw new GamesDomainException(GamesError.InvalidIdempotencyKey, "La IdempotencyKey es obligatoria y de hasta 128 caracteres.");
        }

        var table = options.FindTable(tableId) ?? throw new GamesDomainException(GamesError.TableNotFound, $"No existe la mesa {tableId}.");
        if (stake < table.MinStake || stake > table.MaxStake)
        {
            throw new GamesDomainException(GamesError.InvalidBet, $"En esta mesa la apuesta va de {table.MinStake} a {table.MaxStake} fichas.");
        }

        var betId = BetIdFor(accountId, tableId, idempotencyKey);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await TryPlaceAsync(userId, accountId, table, stake, betId, ct);
            }
            catch (ConcurrencyException) when (attempt < ConflictRetries)
            {
                // Otra apuesta arranco el reloj de la mano al mismo tiempo: se vuelve a leer.
            }
        }
    }

    private async Task<BlackjackBetPlaced> TryPlaceAsync(Guid userId, Guid accountId, BlackjackTableConfig table, long stake, Guid betId, CancellationToken ct)
    {
        await using var session = store.LightweightSession();

        // Reintento con la misma clave: devuelve la misma apuesta (y se rechaza si cambia el contenido).
        var existing = await session.LoadAsync<BlackjackBet>(betId, ct);
        if (existing is not null)
        {
            return await ExistingAsync(session, existing, accountId, table.Id, stake, ct);
        }

        var now = clock.GetUtcNow();
        var round = FirstOrNull(await session.Query<BlackjackRound>().Where(r => r.TableId == table.Id && r.Phase == BlackjackPhase.Betting).OrderByDescending(r => r.OpenedAt).Take(1).ToListAsync(ct));
        if (round is null || (round.BettingEndsAt is { } closes && now >= closes))
        {
            throw new GamesDomainException(GamesError.BettingClosed, "La mesa esta repartiendo. Espera a la proxima mano.");
        }

        var taken = await session.Query<BlackjackBet>().Where(b => b.RoundId == round.Id && b.Status != RoundStatus.Rejected && b.Status != RoundStatus.Voided).ToListAsync(ct);
        if (taken.Any(b => b.UserId == userId))
        {
            throw new GamesDomainException(GamesError.AlreadySeated, "Ya tenes una apuesta en esta mano.");
        }

        if (taken.Count >= options.MaxSeats)
        {
            throw new GamesDomainException(GamesError.BettingClosed, "La mesa esta llena. Espera a la proxima mano.");
        }

        var bet = new BlackjackBet
        {
            Id = betId,
            RoundId = round.Id,
            TableId = table.Id,
            UserId = userId,
            AccountId = accountId,
            Stake = stake,
            Status = RoundStatus.Placed,
            PlacedAt = now,
        };

        try
        {
            if (round.StartBettingClock(now, options.BettingSeconds))
            {
                session.Store(round); // la primera apuesta pone en marcha el reloj: si dos llegan a la vez, una reintenta
            }

            session.Insert(bet);
            await using var outboxSession = outbox.Enroll(session);
            await outboxSession.PublishAsync(new ReserveStake(betId, accountId, stake, ReservationSecondsFor(round, now), "blackjack"));
            await BroadcastAsync(outboxSession, round, now);
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is DocumentAlreadyExistsException || ex.InnerException is DocumentAlreadyExistsException)
        {
            // Dos pedidos iguales a la vez: gano el primero.
            await using var other = store.LightweightSession();
            var winner = await other.LoadAsync<BlackjackBet>(betId, ct);
            if (winner is null)
            {
                throw;
            }

            return await ExistingAsync(other, winner, accountId, table.Id, stake, ct);
        }

        return new BlackjackBetPlaced(betId, round.Id, table.Id, RoundStatus.Placed, round.Commitment, AlreadyPlaced: false);
    }

    /// <summary>Pide una carta. Solo en el turno de esa apuesta y mientras corre su tiempo.</summary>
    public Task<BlackjackBet> HitAsync(Guid userId, Guid betId, CancellationToken ct = default) => ActAsync(userId, betId, hit: true, ct);

    /// <summary>Se planta.</summary>
    public Task<BlackjackBet> StandAsync(Guid userId, Guid betId, CancellationToken ct = default) => ActAsync(userId, betId, hit: false, ct);

    private async Task<BlackjackBet> ActAsync(Guid userId, Guid betId, bool hit, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var session = store.LightweightSession();
        var bet = await session.LoadAsync<BlackjackBet>(betId, ct);
        if (bet is null || bet.UserId != userId)
        {
            throw new GamesDomainException(GamesError.RoundNotFound, $"No existe la apuesta {betId}.");
        }

        if (!bet.IsActive || bet.Hand != HandState.Playing)
        {
            throw new GamesDomainException(GamesError.BetNotActive, "Esa mano ya no esta en juego.");
        }

        var round = await session.LoadAsync<BlackjackRound>(bet.RoundId, ct);
        if (round is not { Phase: BlackjackPhase.Playing } || round.ActiveBetId != bet.Id || round.TurnEndsAt is not { } endsAt || now >= endsAt)
        {
            throw new GamesDomainException(GamesError.NotYourTurn, "No es tu turno (o se te acabo el tiempo).");
        }

        try
        {
            if (hit)
            {
                var shoe = BlackjackMath.Shoe(protector.Unprotect(round.EncryptedSeed, AadFor(round.Id)), round.Id);
                bet.Hit(shoe[round.NextCard]);
                round.NextCard++;
                session.Store(round);
            }
            else
            {
                bet.Stand();
            }

            session.Store(bet);
            await using var outboxSession = outbox.Enroll(session);
            await BroadcastAsync(outboxSession, round, now);
            await session.SaveChangesAsync(ct);
        }
        catch (ConcurrencyException)
        {
            // En el mismo instante se acabo el tiempo del turno y el motor planto la mano: gano esa decision.
            throw new GamesDomainException(GamesError.NotYourTurn, "Se te acabo el tiempo: la mano se planto sola.");
        }

        return bet;
    }

    public async Task<IReadOnlyList<BlackjackTableSummary>> GetTablesAsync(CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        var summaries = new List<BlackjackTableSummary>();
        foreach (var table in options.Tables)
        {
            var round = FirstOrNull(await session.Query<BlackjackRound>().Where(r => r.TableId == table.Id).OrderByDescending(r => r.OpenedAt).Take(1).ToListAsync(ct));
            var players = 0;
            if (round is { IsFinal: false })
            {
                players = await session.Query<BlackjackBet>().CountAsync(b => b.RoundId == round.Id && b.Status != RoundStatus.Rejected && b.Status != RoundStatus.Voided, ct);
            }

            summaries.Add(new BlackjackTableSummary(table, round?.Phase, players));
        }

        return summaries;
    }

    public async Task<BlackjackTableState> GetTableStateAsync(string tableId, CancellationToken ct = default)
    {
        var table = options.FindTable(tableId) ?? throw new GamesDomainException(GamesError.TableNotFound, $"No existe la mesa {tableId}.");
        await using var session = store.QuerySession();
        var round = FirstOrNull(await session.Query<BlackjackRound>().Where(r => r.TableId == table.Id).OrderByDescending(r => r.OpenedAt).Take(1).ToListAsync(ct));
        IReadOnlyList<BlackjackBet> bets = [];
        if (round is not null)
        {
            var all = await session.Query<BlackjackBet>().Where(b => b.RoundId == round.Id && b.Status != RoundStatus.Rejected).ToListAsync(ct);
            bets = [.. all.OrderBy(b => b.Seat == 0 ? int.MaxValue : b.Seat).ThenBy(b => b.PlacedAt)];
        }

        return new BlackjackTableState(clock.GetUtcNow(), table, options, round, bets);
    }

    public async Task<IReadOnlyList<BlackjackBet>> GetHistoryAsync(Guid userId, int limit, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        return await session.Query<BlackjackBet>().Where(b => b.UserId == userId).OrderByDescending(b => b.PlacedAt).Take(limit).ToListAsync(ct);
    }

    // ---- Protocolo de rondas con la Wallet ----

    /// <summary>La Wallet reservo las fichas: la apuesta pasa a estar en juego. Si llego tarde (la mano ya no acepta apuestas), se devuelve lo apostado.</summary>
    public async Task OnStakeReservedAsync(StakeReserved message, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await TryReserveAsync(message.BetId, ct);
                return;
            }
            catch (ConcurrencyException) when (attempt < ConflictRetries)
            {
                // El motor repartio justo ahora: se vuelve a leer y, si ya no acepta apuestas, se devuelve.
            }
        }
    }

    private async Task TryReserveAsync(Guid betId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var session = store.LightweightSession();
        var bet = await session.LoadAsync<BlackjackBet>(betId, ct);
        if (bet is null || bet.Status != RoundStatus.Placed || bet.Reserved)
        {
            return; // no es de Blackjack, o es una entrega repetida
        }

        var round = await session.LoadAsync<BlackjackRound>(bet.RoundId, ct);
        if (round is { Phase: BlackjackPhase.Betting } && (round.BettingEndsAt is null || now < round.BettingEndsAt))
        {
            bet.MarkReserved();
            // Si la reserva llega despues de que la ventana ya se cerro sin nadie reservado (la mano volvio a esperar), esta apuesta es ahora la primera: arranca el reloj.
            round.StartBettingClock(now, options.BettingSeconds);
            session.Store(bet);
            session.Store(round); // fija la version de la mano: si el motor reparte a la vez, esta reserva reintenta y se devuelve
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
    private async Task ResolveAsync(IDocumentSession session, BlackjackBet bet, Func<BlackjackBet, bool> transition, CancellationToken ct)
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

    private async Task CloseAsync(Guid betId, Func<BlackjackBet, DateTimeOffset, bool> transition, CancellationToken ct)
    {
        await using var session = store.LightweightSession();
        var bet = await session.LoadAsync<BlackjackBet>(betId, ct);
        if (bet is null)
        {
            return; // no es de Blackjack
        }

        if (transition(bet, clock.GetUtcNow()))
        {
            session.Store(bet);
            await using var outboxSession = outbox.Enroll(session);
            await outboxSession.PublishAsync(new RoundClosed(
                bet.Id,
                bet.AccountId,
                "Blackjack",
                bet.Status.ToString(),
                WinningNumber: null,
                bet.Stake,
                bet.Status == RoundStatus.Settled ? bet.Payout ?? 0 : 0,
                bet.FailureReason));
            await session.SaveChangesAsync(ct);
            LogClosed(logger, bet.Id, bet.Status);
        }
    }

    private static async Task<BlackjackBetPlaced> ExistingAsync(IDocumentSession session, BlackjackBet existing, Guid accountId, string tableId, long stake, CancellationToken ct)
    {
        if (existing.AccountId != accountId || existing.Stake != stake || existing.TableId != tableId)
        {
            throw new GamesDomainException(GamesError.BetKeyReused, "La IdempotencyKey ya se uso con otra apuesta.");
        }

        var round = await session.LoadAsync<BlackjackRound>(existing.RoundId, ct);
        return new BlackjackBetPlaced(existing.Id, existing.RoundId, existing.TableId, existing.Status, round?.Commitment ?? string.Empty, AlreadyPlaced: true);
    }

    /// <summary>La reserva tiene que durar hasta el final de la mano: lo que falta para apostar + el turno de cada asiento + el crupier + un margen.</summary>
    private int ReservationSecondsFor(BlackjackRound round, DateTimeOffset now)
    {
        var untilDeal = round.BettingEndsAt is { } closes ? Math.Max(0, (closes - now).TotalSeconds) : options.BettingSeconds;
        var longestPlay = (options.MaxSeats * options.TurnSeconds) + (12 * options.DealerStepMilliseconds / 1000.0);
        return (int)Math.Min(900, Math.Ceiling(untilDeal + longestPlay + ReservationMarginSeconds));
    }

    private static T? FirstOrNull<T>(IReadOnlyList<T> items)
        where T : class => items.Count > 0 ? items[0] : null;

    private static string AadFor(Guid roundId) => $"blackjack:{roundId:N}";

    /// <summary>
    /// Aviso a TODOS los jugadores conectados de que la mesa cambio. No lleva cartas ni datos de nadie: el navegador vuelve a pedir el estado de la mesa
    /// (que si lo arma el servidor, con la carta tapada del crupier oculta).
    /// </summary>
    private static ValueTask BroadcastAsync(IOutboxSession outboxSession, BlackjackRound round, DateTimeOffset at) =>
        outboxSession.PublishAsync(new GameBroadcast("blackjack", "tableChanged", JsonSerializer.Serialize(new { tableId = round.TableId, roundId = round.Id, phase = round.Phase.ToString() }, Json), at));

    /// <summary>Id de apuesta determinista por (cuenta, mesa, clave): reintentar con la misma clave apunta a la misma apuesta.</summary>
    internal static Guid BetIdFor(Guid accountId, string tableId, string idempotencyKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"blackjack:{accountId:N}:{tableId}:{idempotencyKey}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Apuesta de Blackjack {BetId} cerrada: {Status}.")]
    private static partial void LogClosed(ILogger logger, Guid betId, RoundStatus status);
}
