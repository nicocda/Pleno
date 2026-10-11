using Casino.BuildingBlocks;
using Casino.Modules.Games.Application;
using Casino.Modules.Games.Blackjack;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Casino.Modules.Games.Api;

/// <summary>El jugador y su cuenta salen del token; la mesa va en la ruta. El cuerpo solo dice cuanto se apuesta.</summary>
public sealed record BlackjackBetBody(long Stake);

public sealed record BlackjackTableResponse(string Id, string Name, long MinStake, long MaxStake, int MaxSeats, string? Phase, int Players);

/// <summary>
/// El crupier tal como lo ve cualquiera: <c>Cards</c> son las cartas a la vista y <c>HiddenCards</c> cuantas hay boca abajo (la tapada NO viaja hasta que se da vuelta).
/// </summary>
public sealed record BlackjackDealerResponse(IReadOnlyList<int> Cards, int HiddenCards);

/// <summary>La mano tal como la ve cualquiera. La semilla es null hasta que termina: recien entonces se revela.</summary>
public sealed record BlackjackRoundResponse(
    Guid Id,
    string TableId,
    string Phase,
    string Commitment,
    DateTimeOffset OpenedAt,
    DateTimeOffset? BettingEndsAt,
    DateTimeOffset? FinishedAt,
    int SeatCount,
    BlackjackDealerResponse Dealer,
    int? ActiveSeat,
    DateTimeOffset? TurnEndsAt,
    string? ServerSeed);

/// <summary>Un asiento de la mesa. La mano es publica (se ve en la mesa) pero no se expone quien es: solo si es la mia (<c>BetId</c> solo viene en la propia).</summary>
public sealed record BlackjackSeatResponse(int? Seat, long Stake, IReadOnlyList<int> Cards, string Hand, string? Result, long? Payout, string Status, bool Mine, Guid? BetId);

public sealed record BlackjackTableStateResponse(
    DateTimeOffset ServerNow,
    BlackjackTableResponse Table,
    double BettingSeconds,
    double TurnSeconds,
    BlackjackRoundResponse? Round,
    IReadOnlyList<BlackjackSeatResponse> Seats);

public sealed record BlackjackBetResponse(
    Guid BetId,
    Guid RoundId,
    string TableId,
    RoundStatus Status,
    long Stake,
    int Seat,
    IReadOnlyList<int> Cards,
    string Hand,
    string? Result,
    long? Payout,
    string? FailureReason,
    DateTimeOffset PlacedAt);

internal static class BlackjackEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";
    private const int MaxHistory = 100;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/games/blackjack").WithTags("Blackjack");
        group.RequireAuthorization(policy => policy.RequireRole(Roles.Player));
        group.AddEndpointFilter(GamesEndpointFilters.MapDomainErrors);

        // Las mesas del casino y como esta cada una.
        group.MapGet("/tables", async (BlackjackService blackjack, CancellationToken ct) =>
            Results.Ok((await blackjack.GetTablesAsync(ct)).Select(s => ToResponse(s.Table, blackjack.Options.MaxSeats, s.Phase, s.Players))));

        // Estado completo de una mesa (y respaldo del tiempo real): la mano en curso y los asientos. La carta tapada del crupier no viaja.
        group.MapGet("/tables/{tableId}", async (string tableId, HttpContext http, BlackjackService blackjack, CancellationToken ct) =>
        {
            var state = await blackjack.GetTableStateAsync(tableId, ct);
            var userId = http.User.GetUserId();
            var players = state.Bets.Count;
            return Results.Ok(new BlackjackTableStateResponse(
                state.ServerNow,
                ToResponse(state.Table, state.Options.MaxSeats, state.Round?.Phase, players),
                state.Options.BettingSeconds,
                state.Options.TurnSeconds,
                state.Round is null ? null : ToResponse(state.Round, state.Bets),
                [.. state.Bets.Select(b => ToSeat(b, userId))]));
        });

        // Sentarse = apostar en la mano abierta. Como en los otros juegos, responde 202 y la reserva en la Wallet es asincrona.
        group.MapPost("/tables/{tableId}/bets", async (string tableId, BlackjackBetBody body, HttpContext http, BlackjackService blackjack, CancellationToken ct) =>
        {
            var userId = http.User.GetUserId();
            var placed = await blackjack.PlaceBetAsync(userId, PlayerIds.WalletAccountFor(userId), tableId, body.Stake, http.Request.Headers[IdempotencyHeader].ToString(), ct);
            return Results.Accepted($"/games/blackjack/bets/{placed.BetId}", placed);
        });

        group.MapPost("/bets/{betId:guid}/hit", async (Guid betId, HttpContext http, BlackjackService blackjack, CancellationToken ct) =>
            Results.Ok(ToResponse(await blackjack.HitAsync(http.User.GetUserId(), betId, ct))));

        group.MapPost("/bets/{betId:guid}/stand", async (Guid betId, HttpContext http, BlackjackService blackjack, CancellationToken ct) =>
            Results.Ok(ToResponse(await blackjack.StandAsync(http.User.GetUserId(), betId, ct))));

        // Solo se ven las apuestas propias. Una ajena responde 404 (no 403) para no revelar que existe.
        group.MapGet("/bets/{betId:guid}", async (Guid betId, HttpContext http, BlackjackService blackjack, CancellationToken ct) =>
        {
            var bet = await blackjack.GetBetAsync(betId, ct);
            if (bet.UserId != http.User.GetUserId())
            {
                throw new GamesDomainException(GamesError.RoundNotFound, $"No existe la apuesta {betId}.");
            }

            return Results.Ok(ToResponse(bet));
        });

        group.MapGet("/bets", async (HttpContext http, BlackjackService blackjack, int? limit, CancellationToken ct) =>
            Results.Ok((await blackjack.GetHistoryAsync(http.User.GetUserId(), Math.Clamp(limit ?? 20, 1, MaxHistory), ct)).Select(ToResponse)));

        // Datos publicos de una mano para verificarla: antes de terminar solo el compromiso; despues, la semilla.
        group.MapGet("/rounds/{roundId:guid}", async (Guid roundId, BlackjackService blackjack, CancellationToken ct) =>
            Results.Ok(ToResponse(await blackjack.GetRoundAsync(roundId, ct), [])));
    }

    private static BlackjackTableResponse ToResponse(BlackjackTableConfig table, int maxSeats, BlackjackPhase? phase, int players) =>
        new(table.Id, table.Name, table.MinStake, table.MaxStake, maxSeats, phase?.ToString(), players);

    private static BlackjackRoundResponse ToResponse(BlackjackRound round, IReadOnlyList<BlackjackBet> bets)
    {
        var hidden = round.Phase == BlackjackPhase.Playing && !round.DealerRevealed ? 1 : 0;
        var active = round.ActiveBetId is { } id ? bets.FirstOrDefault(b => b.Id == id)?.Seat : null;
        return new BlackjackRoundResponse(
            round.Id, round.TableId, round.Phase.ToString(), round.Commitment, round.OpenedAt, round.BettingEndsAt, round.FinishedAt, round.SeatCount,
            new BlackjackDealerResponse(round.DealerCards, hidden), active, round.TurnEndsAt, round.ServerSeed);
    }

    private static BlackjackSeatResponse ToSeat(BlackjackBet bet, Guid userId)
    {
        var mine = bet.UserId == userId;
        return new BlackjackSeatResponse(
            bet.Seat > 0 ? bet.Seat : null, bet.Stake, bet.Cards, bet.Hand.ToString(), bet.Result?.ToString(), bet.Payout, bet.Status.ToString(), mine, mine ? bet.Id : null);
    }

    private static BlackjackBetResponse ToResponse(BlackjackBet bet) => new(
        bet.Id, bet.RoundId, bet.TableId, bet.Status, bet.Stake, bet.Seat, bet.Cards, bet.Hand.ToString(), bet.Result?.ToString(), bet.Payout, bet.FailureReason, bet.PlacedAt);
}
