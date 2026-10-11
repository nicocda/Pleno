using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casino.BuildingBlocks;
using Casino.Contracts;
using Casino.Modules.Games.Application;
using Casino.Modules.Games.Fairness;
using Casino.Modules.Games.Infrastructure;
using Casino.Modules.Games.Platform;
using JasperFx;
using Marten;
using Marten.Exceptions;
using Microsoft.Extensions.Logging;

namespace Casino.Modules.Games.Tables;

/// <summary>Un asiento tal como lo ve cualquiera: sin identidad de nadie (solo "Jugador 2" o "Bot 1"), y si es el propio.</summary>
public sealed record TableSeatView(int Seat, string Name, bool IsBot, bool Mine, bool Ready, bool Away, long? Payout);

/// <summary>La mesa tal como la ve un jugador. <c>Game</c> es lo que ve SU asiento segun las reglas del juego (nunca las cartas ocultas de otros).</summary>
public sealed record TableView(
    DateTimeOffset ServerNow,
    Guid Id,
    string GameId,
    string Name,
    TableStatus Status,
    long BuyIn,
    int MinPlayers,
    int MaxPlayers,
    bool IsOwner,
    bool IsPrivate,
    string? JoinCode,
    string Commitment,
    string? ServerSeed,
    IReadOnlyList<TableSeatView> Seats,
    int? MySeat,
    int? TurnSeat,
    DateTimeOffset? TurnEndsAt,
    object? Game,
    IReadOnlyList<long>? Payouts);

/// <summary>Una mesa en el listado.</summary>
public sealed record TableSummary(Guid Id, string GameId, string Name, long BuyIn, int MaxPlayers, int Players, int Bots, TableStatus Status, bool Mine, bool IsPrivate, DateTimeOffset CreatedAt);

public sealed record TableCreated(Guid TableId, string? JoinCode, bool AlreadyCreated);

/// <summary>
/// La plataforma de los juegos de mesas ENTRE JUGADORES (ADR 0010 y 0012). Se ocupa de todo lo que no son las reglas: crear mesas (publicas o con codigo), sentarse,
/// irse, agregar bots, iniciar, aplicar las jugadas en orden, los turnos con tiempo limite, la semilla provably fair y las fichas (cada asiento humano es una apuesta
/// del protocolo de rondas con la Wallet: reserva al sentarse, liquida con lo que se llevo al terminar). Cada juego solo aporta sus reglas (<see cref="ITableGame"/>).
/// </summary>
public sealed partial class TableService(
    IDocumentStore store,
    IOutboxFactory outbox,
    TimeProvider clock,
    SeedProtector protector,
    IEnumerable<ITableGame> games,
    TablesOptions options,
    ILogger<TableService> logger) : IGameRounds
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const int ConflictRetries = 5;
    private const int MissesBeforeAway = 3;
    private const int MaxNameLength = 40;
    private const int JoinCodeLength = 6;

    private readonly Dictionary<string, ITableGame> _games = games.ToDictionary(g => g.Info.Id, StringComparer.Ordinal);

    public TablesOptions Options => options;

    public ITableGame GameOf(string gameId) =>
        _games.TryGetValue(gameId, out var game) ? game : throw new GamesDomainException(GamesError.RoundNotFound, $"No existe el juego {gameId}.");

    // ---- Lo que hace el jugador ----

    public async Task<TableCreated> CreateAsync(Guid userId, Guid accountId, string gameId, long buyIn, int maxPlayers, bool isPrivate, string? name, string idempotencyKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
        {
            throw new GamesDomainException(GamesError.InvalidIdempotencyKey, "La IdempotencyKey es obligatoria y de hasta 128 caracteres.");
        }

        var game = GameOf(gameId);
        if (buyIn < game.MinBuyIn || buyIn > game.MaxBuyIn)
        {
            throw new GamesDomainException(GamesError.InvalidBet, $"La entrada va de {game.MinBuyIn} a {game.MaxBuyIn} fichas.");
        }

        if (maxPlayers < game.MinPlayers || maxPlayers > game.MaxPlayers)
        {
            throw new GamesDomainException(GamesError.InvalidBet, $"La mesa es de {game.MinPlayers} a {game.MaxPlayers} jugadores.");
        }

        var tableId = TableIdFor(userId, gameId, idempotencyKey);
        await using var session = store.LightweightSession();
        var existing = await session.LoadAsync<PlayerTable>(tableId, ct);
        if (existing is not null)
        {
            return new TableCreated(tableId, existing.OwnerUserId == userId ? existing.JoinCode : null, AlreadyCreated: true);
        }

        await EnsureNotSeatedAsync(session, gameId, userId, ct);

        var now = clock.GetUtcNow();
        var seed = ProvablyFair.GenerateServerSeed();
        var table = new PlayerTable
        {
            Id = tableId,
            GameId = gameId,
            Name = CleanName(name) ?? $"Mesa de {game.Info.Name}",
            OwnerUserId = userId,
            IsPrivate = isPrivate,
            JoinCode = isPrivate ? NewJoinCode() : null,
            BuyIn = buyIn,
            MaxPlayers = maxPlayers,
            Status = TableStatus.Open,
            Commitment = ProvablyFair.Commitment(seed),
            EncryptedSeed = protector.Protect(seed, AadFor(tableId)),
            CreatedAt = now,
        };
        var bet = SitDown(table, userId, accountId, now);

        session.Insert(table);
        session.Insert(bet);
        await using var outboxSession = outbox.Enroll(session);
        await outboxSession.PublishAsync(new ReserveStake(bet.Id, accountId, buyIn, options.ReservationSeconds, gameId));
        await BroadcastAsync(outboxSession, table, now);
        await session.SaveChangesAsync(ct);
        return new TableCreated(tableId, table.JoinCode, AlreadyCreated: false);
    }

    public Task JoinAsync(Guid userId, Guid accountId, Guid tableId, string? code, CancellationToken ct = default) =>
        RetryAsync(async () =>
        {
            await using var session = store.LightweightSession();
            var table = await LoadAsync(session, tableId, ct);
            if (table.SeatOf(userId) >= 0)
            {
                return; // ya esta sentado: reintento idempotente
            }

            if (table.Status != TableStatus.Open)
            {
                throw new GamesDomainException(GamesError.TableClosed, "La mesa ya empezo o termino.");
            }

            if (table.IsPrivate && !string.Equals(table.JoinCode, code?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new GamesDomainException(GamesError.InvalidAction, "El codigo de la mesa no es correcto.");
            }

            if (table.Seats.Count >= table.MaxPlayers)
            {
                throw new GamesDomainException(GamesError.TableClosed, "La mesa esta llena.");
            }

            await EnsureNotSeatedAsync(session, table.GameId, userId, ct);

            var now = clock.GetUtcNow();
            var bet = SitDown(table, userId, accountId, now);
            session.Store(table);
            session.Insert(bet);
            await using var outboxSession = outbox.Enroll(session);
            await outboxSession.PublishAsync(new ReserveStake(bet.Id, accountId, table.BuyIn, options.ReservationSeconds, table.GameId));
            await BroadcastAsync(outboxSession, table, now);
            await session.SaveChangesAsync(ct);
        });

    /// <summary>Entra a una mesa privada con solo su codigo (la privada no se lista ni se revela: el codigo es la unica forma de encontrarla).</summary>
    public async Task<Guid> JoinByCodeAsync(Guid userId, Guid accountId, string gameId, string code, CancellationToken ct = default)
    {
        GameOf(gameId);
        var clean = code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (clean.Length != JoinCodeLength)
        {
            throw new GamesDomainException(GamesError.InvalidAction, "El codigo de la mesa no es correcto.");
        }

        await using var query = store.QuerySession();
        var found = await query.Query<PlayerTable>().Where(t => t.GameId == gameId && t.IsPrivate && t.JoinCode == clean && t.Status == TableStatus.Open).FirstOrDefaultAsync(ct)
            ?? throw new GamesDomainException(GamesError.InvalidAction, "El codigo de la mesa no es correcto.");
        await JoinAsync(userId, accountId, found.Id, clean, ct);
        return found.Id;
    }

    /// <summary>Se va de una mesa que todavia no empezo (se devuelven las fichas). En plena partida no se puede: si no jugas, juega un bot por vos.</summary>
    public Task LeaveAsync(Guid userId, Guid tableId, CancellationToken ct = default) =>
        RetryAsync(async () =>
        {
            await using var session = store.LightweightSession();
            var table = await LoadAsync(session, tableId, ct);
            var seat = table.SeatOf(userId);
            if (seat < 0)
            {
                return;
            }

            if (table.Status != TableStatus.Open)
            {
                throw new GamesDomainException(GamesError.TableClosed, "No podes irte en plena partida: si no jugas, un bot juega por vos.");
            }

            var now = clock.GetUtcNow();
            var betId = table.Seats[seat].BetId!.Value;
            table.Seats.RemoveAt(seat);
            table.SyncHumans();
            if (table.HumanCount == 0)
            {
                table.Status = TableStatus.Cancelled;
                table.FinishedAt = now;
            }
            else if (table.OwnerUserId == userId)
            {
                table.OwnerUserId = table.Seats.First(s => !s.IsBot).UserId!.Value;
            }

            session.Store(table);
            var bet = await session.LoadAsync<TableBet>(betId, ct);
            await using var outboxSession = outbox.Enroll(session);
            if (bet is not null)
            {
                // Si la Wallet ya reservo, se devuelve ahora; si todavia no, se devuelve cuando llegue la reserva (la mesa ya no tiene ese asiento).
                if (bet.Reserved && bet.Refund("LeftTable"))
                {
                    session.Store(bet);
                    await outboxSession.PublishAsync(new RoundResolved(bet.Id, bet.AccountId, bet.Payout ?? 0));
                }
            }

            await BroadcastAsync(outboxSession, table, now);
            await session.SaveChangesAsync(ct);
        });

    /// <summary>El dueño agrega un bot a una mesa abierta (la casa respalda lo que pone un bot).</summary>
    public Task AddBotAsync(Guid userId, Guid tableId, CancellationToken ct = default) =>
        RetryAsync(async () =>
        {
            await using var session = store.LightweightSession();
            var table = await OpenTableOfOwnerAsync(session, tableId, userId, ct);
            if (table.Seats.Count >= table.MaxPlayers)
            {
                throw new GamesDomainException(GamesError.TableClosed, "La mesa esta llena.");
            }

            table.Seats.Add(new TableSeat { IsBot = true });
            session.Store(table);
            await using var outboxSession = outbox.Enroll(session);
            await BroadcastAsync(outboxSession, table, clock.GetUtcNow());
            await session.SaveChangesAsync(ct);
        });

    public Task RemoveBotAsync(Guid userId, Guid tableId, CancellationToken ct = default) =>
        RetryAsync(async () =>
        {
            await using var session = store.LightweightSession();
            var table = await OpenTableOfOwnerAsync(session, tableId, userId, ct);
            var index = table.Seats.FindLastIndex(s => s.IsBot);
            if (index < 0)
            {
                return;
            }

            table.Seats.RemoveAt(index);
            session.Store(table);
            await using var outboxSession = outbox.Enroll(session);
            await BroadcastAsync(outboxSession, table, clock.GetUtcNow());
            await session.SaveChangesAsync(ct);
        });

    /// <summary>El dueño inicia la partida: hace falta el minimo de asientos y que las fichas de todos los humanos ya esten reservadas.</summary>
    public Task StartAsync(Guid userId, Guid tableId, CancellationToken ct = default) =>
        RetryAsync(async () =>
        {
            await using var session = store.LightweightSession();
            var table = await OpenTableOfOwnerAsync(session, tableId, userId, ct);
            var game = GameOf(table.GameId);
            if (table.Seats.Count < game.MinPlayers)
            {
                throw new GamesDomainException(GamesError.TableClosed, $"Hacen falta al menos {game.MinPlayers} jugadores (podes sumar bots).");
            }

            var reserved = await session.Query<TableBet>().CountAsync(b => b.TableId == tableId && b.Status == RoundStatus.Placed && b.Reserved, ct);
            if (reserved != table.HumanCount)
            {
                throw new GamesDomainException(GamesError.TableClosed, "Todavia se estan confirmando las fichas de algun jugador. Proba de nuevo en un instante.");
            }

            var now = clock.GetUtcNow();
            var seed = protector.Unprotect(table.EncryptedSeed, AadFor(table.Id));
            var state = game.Start(new GameStartContext(table.Seats.Count, seed, table.Id, table.BuyIn, now));
            table.Status = TableStatus.Playing;
            table.StartedAt = now;
            await AdvanceAsync(session, table, game, state, now, ct);
            await session.SaveChangesAsync(ct);
        });

    /// <summary>Una jugada del jugador (es el turno de su asiento).</summary>
    public Task ActAsync(Guid userId, Guid tableId, string action, CancellationToken ct = default) =>
        RetryAsync(async () =>
        {
            await using var session = store.LightweightSession();
            var table = await LoadAsync(session, tableId, ct);
            var seat = table.SeatOf(userId);
            if (seat < 0)
            {
                throw new GamesDomainException(GamesError.RoundNotFound, $"No existe la mesa {tableId}.");
            }

            if (table.Status != TableStatus.Playing)
            {
                throw new GamesDomainException(GamesError.TableClosed, "La partida no esta en juego.");
            }

            if (table.TurnSeat != seat)
            {
                throw new GamesDomainException(GamesError.NotYourTurn, "No es tu turno.");
            }

            var now = clock.GetUtcNow();
            var game = GameOf(table.GameId);
            table.Seats[seat].Misses = 0;
            table.Seats[seat].Away = false;
            await AdvanceAsync(session, table, game, ApplyOrThrow(game, table.StateJson!, seat, action, now), now, ct);
            await session.SaveChangesAsync(ct);
        });

    public async Task<TableView> GetViewAsync(Guid userId, Guid tableId, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        var table = await session.LoadAsync<PlayerTable>(tableId, ct) ?? throw new GamesDomainException(GamesError.RoundNotFound, $"No existe la mesa {tableId}.");
        var mySeat = table.SeatOf(userId);
        if (mySeat < 0 && table.IsPrivate)
        {
            throw new GamesDomainException(GamesError.RoundNotFound, $"No existe la mesa {tableId}."); // una privada no se revela a quien no esta sentado
        }

        var game = GameOf(table.GameId);
        var bets = await session.Query<TableBet>().Where(b => b.TableId == tableId).ToListAsync(ct);
        var readyBets = bets.Where(b => b.Reserved || b.Status is RoundStatus.Resolved or RoundStatus.Settled).Select(b => b.Id).ToHashSet();
        var seats = new List<TableSeatView>();
        for (var i = 0; i < table.Seats.Count; i++)
        {
            var seat = table.Seats[i];
            var ready = seat.IsBot || (seat.BetId is { } betId && readyBets.Contains(betId));
            seats.Add(new TableSeatView(i, seat.IsBot ? $"Bot {i + 1}" : $"Jugador {i + 1}", seat.IsBot, i == mySeat, ready, seat.Away, table.Payouts is { } p && i < p.Count ? p[i] : null));
        }

        object? view = table.StateJson is null ? null : game.View(table.StateJson, mySeat >= 0 ? mySeat : null);
        var isOwner = table.OwnerUserId == userId;
        return new TableView(
            clock.GetUtcNow(), table.Id, table.GameId, table.Name, table.Status, table.BuyIn, game.MinPlayers, table.MaxPlayers, isOwner, table.IsPrivate,
            isOwner ? table.JoinCode : null, table.Commitment, table.ServerSeed, seats, mySeat >= 0 ? mySeat : null, table.TurnSeat, table.TurnEndsAt, view, table.Payouts);
    }

    /// <summary>Las mesas abiertas (publicas) del juego, mas las propias en curso.</summary>
    public async Task<IReadOnlyList<TableSummary>> ListAsync(string gameId, Guid userId, CancellationToken ct = default)
    {
        GameOf(gameId);
        await using var session = store.QuerySession();
        var open = await session.Query<PlayerTable>().Where(t => t.GameId == gameId && t.Status == TableStatus.Open && !t.IsPrivate).OrderByDescending(t => t.CreatedAt).Take(50).ToListAsync(ct);
        var mine = await session.Query<PlayerTable>().Where(t => t.GameId == gameId && t.Humans.Contains(userId) && (t.Status == TableStatus.Open || t.Status == TableStatus.Playing)).ToListAsync(ct);
        return [.. open.Concat(mine.Where(m => open.All(o => o.Id != m.Id))).Select(t => Summary(t, userId))];
    }

    public async Task<IReadOnlyList<TableSummary>> GetHistoryAsync(string gameId, Guid userId, int limit, CancellationToken ct = default)
    {
        GameOf(gameId);
        await using var session = store.QuerySession();
        var tables = await session.Query<PlayerTable>().Where(t => t.GameId == gameId && t.Humans.Contains(userId) && (t.Status == TableStatus.Finished || t.Status == TableStatus.Cancelled))
            .OrderByDescending(t => t.CreatedAt).Take(limit).ToListAsync(ct);
        return [.. tables.Select(t => Summary(t, userId))];
    }

    // ---- Lo que hace el motor (bots, turnos vencidos, mesas viejas) ----

    /// <summary>Procesa lo que vencio: los bots que tienen que jugar, los jugadores que dejaron pasar su turno y las mesas abiertas que nadie inicio.</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        await using var query = store.QuerySession();
        var due = await query.Query<PlayerTable>().Where(t => t.Status == TableStatus.Playing && t.NextAutoAt != null && t.NextAutoAt <= now).Select(t => t.Id).ToListAsync(ct);
        var staleBefore = now.AddMinutes(-options.OpenTableMinutes);
        var stale = await query.Query<PlayerTable>().Where(t => t.Status == TableStatus.Open && t.CreatedAt < staleBefore).Select(t => t.Id).ToListAsync(ct);

        await Task.WhenAll(due.Select(id => SafelyAsync(() => AutoPlayAsync(id, ct), id)).Concat(stale.Select(id => SafelyAsync(() => CancelAsync(id, "OpenTableExpired", ct), id))));
    }

    /// <summary>Una jugada automatica en el turno que vencio: la hace el bot (o un bot por el jugador ausente) o la de "no actuo a tiempo".</summary>
    public Task AutoPlayAsync(Guid tableId, CancellationToken ct = default) =>
        RetryAsync(async () =>
        {
            await using var session = store.LightweightSession();
            var table = await session.LoadAsync<PlayerTable>(tableId, ct);
            var now = clock.GetUtcNow();
            if (table is not { Status: TableStatus.Playing, TurnSeat: { } seatNo } || table.NextAutoAt is not { } due || due > now)
            {
                return; // ya actuo alguien o ya termino
            }

            var game = GameOf(table.GameId);
            var seat = table.Seats[seatNo];
            string action;
            if (seat.IsBot || seat.Away)
            {
                action = game.BotAction(table.StateJson!, seatNo);
            }
            else
            {
                action = game.TimeoutAction(table.StateJson!, seatNo);
                seat.Misses++;
                seat.Away = seat.Misses >= MissesBeforeAway;
            }

            await AdvanceAsync(session, table, game, ApplyOrThrow(game, table.StateJson!, seatNo, action, now), now, ct);
            await session.SaveChangesAsync(ct);
        });

    /// <summary>Cancela una mesa que no termino bien (se devuelve lo apostado a quien ya habia reservado).</summary>
    public Task CancelAsync(Guid tableId, string reason, CancellationToken ct = default) =>
        RetryAsync(async () =>
        {
            await using var session = store.LightweightSession();
            var table = await session.LoadAsync<PlayerTable>(tableId, ct);
            if (table is null || table.IsFinal)
            {
                return;
            }

            var now = clock.GetUtcNow();
            table.Status = TableStatus.Cancelled;
            table.FinishedAt = now;
            table.NextAutoAt = null;
            table.TurnEndsAt = null;
            table.TurnSeat = null;
            session.Store(table);
            await using var outboxSession = outbox.Enroll(session);
            foreach (var bet in await session.Query<TableBet>().Where(b => b.TableId == tableId && b.Status == RoundStatus.Placed && b.Reserved).ToListAsync(ct))
            {
                if (bet.Refund(reason))
                {
                    session.Store(bet);
                    await outboxSession.PublishAsync(new RoundResolved(bet.Id, bet.AccountId, bet.Payout ?? 0));
                }
            }

            await BroadcastAsync(outboxSession, table, now);
            await session.SaveChangesAsync(ct);
        });

    // ---- Protocolo de rondas con la Wallet ----

    /// <summary>La Wallet reservo las fichas de un asiento. Si el jugador ya se fue (o la mesa ya no existe), se devuelve lo apostado.</summary>
    public async Task OnStakeReservedAsync(StakeReserved message, CancellationToken ct = default)
    {
        await using var session = store.LightweightSession();
        var bet = await session.LoadAsync<TableBet>(message.BetId, ct);
        if (bet is null || bet.Status != RoundStatus.Placed || bet.Reserved)
        {
            return; // no es de una mesa, o es una entrega repetida
        }

        var table = await session.LoadAsync<PlayerTable>(bet.TableId, ct);
        var stillSeated = table is { IsFinal: false } && table.Seats.Any(s => s.BetId == bet.Id);
        if (stillSeated)
        {
            bet.MarkReserved();
            session.Store(bet);
            await using var outboxSession = outbox.Enroll(session);
            await BroadcastAsync(outboxSession, table!, clock.GetUtcNow());
            await session.SaveChangesAsync(ct);
            return;
        }

        await ResolveAsync(session, bet, b => b.Refund("LeftTable"), ct);
    }

    public Task OnStakeSettledAsync(StakeSettled message, CancellationToken ct = default) =>
        CloseAsync(message.BetId, (bet, now) => bet.MarkSettled(now), ct);

    /// <summary>La Wallet rechazo la reserva (saldo insuficiente): el asiento se libera.</summary>
    public async Task OnStakeRejectedAsync(StakeRejected message, CancellationToken ct = default)
    {
        await CloseAsync(message.BetId, (bet, now) => bet.MarkRejected(message.Reason, now), ct);
        await RetryAsync(async () =>
        {
            await using var session = store.LightweightSession();
            var bet = await session.LoadAsync<TableBet>(message.BetId, ct);
            if (bet is null)
            {
                return;
            }

            var table = await session.LoadAsync<PlayerTable>(bet.TableId, ct);
            if (table is not { Status: TableStatus.Open })
            {
                return;
            }

            var seat = table.Seats.FindIndex(s => s.BetId == bet.Id);
            if (seat < 0)
            {
                return;
            }

            table.Seats.RemoveAt(seat);
            table.SyncHumans();
            if (table.HumanCount == 0)
            {
                table.Status = TableStatus.Cancelled;
                table.FinishedAt = clock.GetUtcNow();
            }
            else if (table.OwnerUserId == bet.UserId)
            {
                table.OwnerUserId = table.Seats.First(s => !s.IsBot).UserId!.Value;
            }

            session.Store(table);
            await using var outboxSession = outbox.Enroll(session);
            await BroadcastAsync(outboxSession, table, clock.GetUtcNow());
            await session.SaveChangesAsync(ct);
        });
    }

    public Task OnStakeReleasedAsync(StakeReleased message, CancellationToken ct = default) =>
        CloseAsync(message.BetId, (bet, now) => bet.MarkVoided("ReservationExpired", now), ct);

    public Task OnSettlementRejectedAsync(StakeSettlementRejected message, CancellationToken ct = default) =>
        CloseAsync(message.BetId, (bet, now) => bet.MarkVoided(message.Reason, now), ct);

    // ---- Interno ----

    /// <summary>Guarda el estado nuevo, decide el proximo turno (o termina y paga) y avisa a todos.</summary>
    private async Task AdvanceAsync(IDocumentSession session, PlayerTable table, ITableGame game, string state, DateTimeOffset now, CancellationToken ct)
    {
        table.StateJson = state;
        var outcome = game.Outcome(state);
        await using var outboxSession = outbox.Enroll(session);
        if (outcome is null)
        {
            var turn = game.Turn(state) ?? throw new InvalidOperationException($"El juego {table.GameId} no termino pero tampoco tiene turno.");
            var seat = table.Seats[turn.Seat];
            table.TurnSeat = turn.Seat;
            if (seat.IsBot || seat.Away)
            {
                table.TurnEndsAt = null;
                table.NextAutoAt = now.AddMilliseconds(options.BotThinkMilliseconds);
            }
            else
            {
                table.TurnEndsAt = now.AddSeconds(turn.Seconds);
                table.NextAutoAt = table.TurnEndsAt;
            }
        }
        else
        {
            await FinishAsync(session, outboxSession, table, outcome, now, ct);
        }

        session.Store(table);
        await BroadcastAsync(outboxSession, table, now);
    }

    /// <summary>La partida termino: se revela la semilla y cada asiento humano cobra lo que se llevo.</summary>
    private async Task FinishAsync(IDocumentSession session, IOutboxSession outboxSession, PlayerTable table, GameOutcome outcome, DateTimeOffset now, CancellationToken ct)
    {
        var payouts = outcome.Payouts.ToList();
        var expected = table.BuyIn * table.Seats.Count;
        if (payouts.Count != table.Seats.Count || payouts.Any(p => p < 0) || payouts.Sum() != expected)
        {
            // Una cuenta que no cierra es un error del juego: nadie pierde por eso, se devuelve la entrada de cada uno.
            LogPayoutsMismatch(logger, table.GameId, table.Id, expected, payouts.Sum());
            payouts = [.. table.Seats.Select(_ => table.BuyIn)];
        }

        table.Status = TableStatus.Finished;
        table.FinishedAt = now;
        table.ServerSeed = protector.Unprotect(table.EncryptedSeed, AadFor(table.Id));
        table.Payouts = payouts;
        table.TurnSeat = null;
        table.TurnEndsAt = null;
        table.NextAutoAt = null;

        var bets = (await session.Query<TableBet>().Where(b => b.TableId == table.Id && b.Status == RoundStatus.Placed && b.Reserved).ToListAsync(ct)).ToDictionary(b => b.Id);
        for (var i = 0; i < table.Seats.Count; i++)
        {
            if (table.Seats[i].BetId is { } betId && bets.TryGetValue(betId, out var bet) && bet.Finish(payouts[i]))
            {
                session.Store(bet);
                await outboxSession.PublishAsync(new RoundResolved(bet.Id, bet.AccountId, payouts[i]));
            }
        }
    }

    private static string ApplyOrThrow(ITableGame game, string state, int seat, string action, DateTimeOffset now)
    {
        try
        {
            return game.Apply(state, seat, action, now);
        }
        catch (TableRuleException ex)
        {
            throw new GamesDomainException(GamesError.InvalidAction, ex.Message);
        }
    }

    private static TableSummary Summary(PlayerTable table, Guid userId) =>
        new(table.Id, table.GameId, table.Name, table.BuyIn, table.MaxPlayers, table.HumanCount, table.Seats.Count(s => s.IsBot), table.Status, table.SeatOf(userId) >= 0, table.IsPrivate, table.CreatedAt);

    private static TableBet SitDown(PlayerTable table, Guid userId, Guid accountId, DateTimeOffset now)
    {
        var betId = BetIdFor(table.Id, userId);
        table.Seats.Add(new TableSeat { UserId = userId, AccountId = accountId, BetId = betId });
        table.SyncHumans();
        return new TableBet { Id = betId, TableId = table.Id, GameId = table.GameId, UserId = userId, AccountId = accountId, Stake = table.BuyIn, Status = RoundStatus.Placed, PlacedAt = now };
    }

    private static async Task<PlayerTable> LoadAsync(IDocumentSession session, Guid tableId, CancellationToken ct) =>
        await session.LoadAsync<PlayerTable>(tableId, ct) ?? throw new GamesDomainException(GamesError.RoundNotFound, $"No existe la mesa {tableId}.");

    private static async Task<PlayerTable> OpenTableOfOwnerAsync(IDocumentSession session, Guid tableId, Guid userId, CancellationToken ct)
    {
        var table = await LoadAsync(session, tableId, ct);
        if (table.OwnerUserId != userId)
        {
            throw new GamesDomainException(GamesError.RoundNotFound, $"No existe la mesa {tableId}.");
        }

        if (table.Status != TableStatus.Open)
        {
            throw new GamesDomainException(GamesError.TableClosed, "La mesa ya empezo o termino.");
        }

        return table;
    }

    private static async Task EnsureNotSeatedAsync(IDocumentSession session, string gameId, Guid userId, CancellationToken ct)
    {
        var active = await session.Query<PlayerTable>().AnyAsync(t => t.GameId == gameId && t.Humans.Contains(userId) && (t.Status == TableStatus.Open || t.Status == TableStatus.Playing), ct);
        if (active)
        {
            throw new GamesDomainException(GamesError.AlreadySeated, "Ya estas sentado en una mesa de este juego. Termina esa partida o salite antes de entrar a otra.");
        }
    }

    /// <summary>Dos jugadas (o un jugador y el motor) a la vez sobre la misma mesa: gana el primero y el otro vuelve a leer y reintenta con el estado nuevo.</summary>
    private static async Task RetryAsync(Func<Task> operation)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await operation();
                return;
            }
            catch (ConcurrencyException) when (attempt < ConflictRetries)
            {
                // se vuelve a leer todo
            }
        }
    }

    private async Task SafelyAsync(Func<Task> operation, Guid tableId)
    {
        try
        {
            await operation();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogTableFailed(logger, tableId, ex);
        }
    }

    /// <summary>Aplica una transicion y, en la MISMA transaccion, avisa a la Wallet lo que se lleva el asiento para que liquide.</summary>
    private async Task ResolveAsync(IDocumentSession session, TableBet bet, Func<TableBet, bool> transition, CancellationToken ct)
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

    private async Task CloseAsync(Guid betId, Func<TableBet, DateTimeOffset, bool> transition, CancellationToken ct)
    {
        await using var session = store.LightweightSession();
        var bet = await session.LoadAsync<TableBet>(betId, ct);
        if (bet is null)
        {
            return; // no es de una mesa
        }

        if (transition(bet, clock.GetUtcNow()))
        {
            session.Store(bet);
            await using var outboxSession = outbox.Enroll(session);
            await outboxSession.PublishAsync(new RoundClosed(
                bet.Id,
                bet.AccountId,
                GameOf(bet.GameId).Info.Name,
                bet.Status.ToString(),
                WinningNumber: null,
                bet.Stake,
                bet.Status == RoundStatus.Settled ? bet.Payout ?? 0 : 0,
                bet.FailureReason));
            await session.SaveChangesAsync(ct);
            LogClosed(logger, bet.Id, bet.Status);
        }
    }

    private static string? CleanName(string? name)
    {
        var clean = name?.Trim();
        if (string.IsNullOrEmpty(clean))
        {
            return null;
        }

        clean = new string([.. clean.Where(c => !char.IsControl(c))]);
        return clean.Length > MaxNameLength ? clean[..MaxNameLength] : clean;
    }

    private static string NewJoinCode() => new([.. Enumerable.Range(0, JoinCodeLength).Select(_ => "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"[RandomNumberGenerator.GetInt32(32)])]);

    private static string AadFor(Guid tableId) => $"table:{tableId:N}";

    /// <summary>
    /// Aviso a TODOS los jugadores conectados de que una mesa cambio. No lleva cartas ni datos de nadie: el navegador vuelve a pedir lo suyo
    /// (la vista de SU asiento, que arma el servidor) y el listado de mesas abiertas.
    /// </summary>
    private static ValueTask BroadcastAsync(IOutboxSession outboxSession, PlayerTable table, DateTimeOffset at) =>
        outboxSession.PublishAsync(new GameBroadcast(table.GameId, "tableChanged", JsonSerializer.Serialize(new { tableId = table.Id, gameId = table.GameId, status = table.Status.ToString() }, Json), at));

    /// <summary>Id de mesa determinista por (jugador, juego, clave): reintentar crear con la misma clave apunta a la misma mesa.</summary>
    internal static Guid TableIdFor(Guid userId, string gameId, string idempotencyKey) => HashGuid($"table:{userId:N}:{gameId}:{idempotencyKey}");

    /// <summary>Id de la apuesta de un jugador en una mesa: una por jugador y mesa.</summary>
    internal static Guid BetIdFor(Guid tableId, Guid userId) => HashGuid($"tablebet:{tableId:N}:{userId:N}");

    private static Guid HashGuid(string text) => new(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16));

    [LoggerMessage(Level = LogLevel.Debug, Message = "Apuesta de mesa {BetId} cerrada: {Status}.")]
    private static partial void LogClosed(ILogger logger, Guid betId, RoundStatus status);

    [LoggerMessage(Level = LogLevel.Error, Message = "El juego {GameId} dio un pago que no cierra en la mesa {TableId}: se esperaba {Expected} y sumo {Actual}. Se devuelve la entrada de cada uno.")]
    private static partial void LogPayoutsMismatch(ILogger logger, string gameId, Guid tableId, long expected, long actual);

    [LoggerMessage(Level = LogLevel.Error, Message = "Fallo el procesamiento automatico de la mesa {TableId}.")]
    private static partial void LogTableFailed(ILogger logger, Guid tableId, Exception exception);
}
