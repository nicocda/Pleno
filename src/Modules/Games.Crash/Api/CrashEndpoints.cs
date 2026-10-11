using Casino.BuildingBlocks;
using Casino.Modules.Games.Application;
using Casino.Modules.Games.Crash;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Casino.Modules.Games.Api;

/// <summary>El jugador y su cuenta salen del token: el cuerpo solo dice cuanto se apuesta y, si quiere, en que multiplicador retirar solo.</summary>
public sealed record CrashBetBody(long Stake, decimal? AutoCashOut = null);

/// <summary>La ronda tal como la ve cualquiera. El punto de explosion y la semilla son null hasta que explota: recien entonces se revelan.</summary>
public sealed record CrashRoundResponse(
    Guid Id,
    string Phase,
    string Commitment,
    DateTimeOffset OpenedAt,
    DateTimeOffset BettingEndsAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CrashedAt,
    long? CrashPoint,
    string? ServerSeed,
    int EdgePermille,
    double GrowthPerSecond);

public sealed record CrashBetResponse(
    Guid BetId,
    Guid RoundId,
    RoundStatus Status,
    long Stake,
    long? AutoCashOut,
    bool InPlay,
    long? CashedOutAt,
    long? Payout,
    string? FailureReason,
    DateTimeOffset PlacedAt);

public sealed record CrashStateResponse(
    DateTimeOffset ServerNow,
    double GrowthPerSecond,
    long MinStake,
    long MaxStake,
    CrashRoundResponse? Round,
    CrashBetResponse? MyBet,
    IReadOnlyList<CrashRoundResponse> History);

internal static class CrashEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";
    private const int MaxHistory = 100;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/games/crash").WithTags("Crash");
        group.RequireAuthorization(policy => policy.RequireRole(Roles.Player));
        group.AddEndpointFilter(GamesEndpointFilters.MapDomainErrors);

        // Estado completo para entrar al juego (y como respaldo del tiempo real): la ronda en curso, mi apuesta y las ultimas explosiones.
        group.MapGet("/state", async (HttpContext http, CrashService crash, CancellationToken ct) =>
        {
            var state = await crash.GetStateAsync(http.User.GetUserId(), ct);
            return Results.Ok(new CrashStateResponse(
                state.ServerNow, state.GrowthPerSecond, state.MinStake, state.MaxStake,
                state.Round is null ? null : ToResponse(state.Round),
                state.MyBet is null ? null : ToResponse(state.MyBet),
                [.. state.History.Select(ToResponse)]));
        });

        // Se apuesta en la ronda abierta. Como en los otros juegos, responde 202 y la apuesta se reserva en la Wallet de forma asincrona.
        group.MapPost("/bets", async (CrashBetBody body, HttpContext http, CrashService crash, CancellationToken ct) =>
        {
            var userId = http.User.GetUserId();
            long? auto = null;
            if (body.AutoCashOut is { } multiplier)
            {
                if (multiplier is <= 1m or > 1000m)
                {
                    throw new GamesDomainException(GamesError.InvalidBet, "El retiro automatico va de x1,01 a x100,00.");
                }

                auto = (long)Math.Floor(multiplier * 100m);
            }

            var placed = await crash.PlaceBetAsync(userId, PlayerIds.WalletAccountFor(userId), body.Stake, auto, http.Request.Headers[IdempotencyHeader].ToString(), ct);
            return Results.Accepted($"/games/crash/bets/{placed.BetId}", placed);
        });

        // Retirar: el servidor decide el multiplicador por su reloj.
        group.MapPost("/bets/{betId:guid}/cashout", async (Guid betId, HttpContext http, CrashService crash, CancellationToken ct) =>
            Results.Ok(await crash.CashOutAsync(http.User.GetUserId(), betId, ct)));

        // Solo se ven las apuestas propias. Una ajena responde 404 (no 403) para no revelar que existe.
        group.MapGet("/bets/{betId:guid}", async (Guid betId, HttpContext http, CrashService crash, CancellationToken ct) =>
        {
            var bet = await crash.GetBetAsync(betId, ct);
            if (bet.UserId != http.User.GetUserId())
            {
                throw new GamesDomainException(GamesError.RoundNotFound, $"No existe la apuesta {betId}.");
            }

            return Results.Ok(ToResponse(bet));
        });

        group.MapGet("/bets", async (HttpContext http, CrashService crash, int? limit, CancellationToken ct) =>
            Results.Ok((await crash.GetHistoryAsync(http.User.GetUserId(), Math.Clamp(limit ?? 20, 1, MaxHistory), ct)).Select(ToResponse)));

        // Datos publicos de una ronda para verificarla: antes de explotar solo el compromiso; despues, la semilla y el punto.
        group.MapGet("/rounds/{roundId:guid}", async (Guid roundId, CrashService crash, CancellationToken ct) =>
            Results.Ok(ToResponse(await crash.GetRoundAsync(roundId, ct))));

        group.MapGet("/rounds", async (CrashService crash, int? limit, CancellationToken ct) =>
            Results.Ok((await crash.GetRecentRoundsAsync(Math.Clamp(limit ?? 20, 1, MaxHistory), ct)).Select(ToResponse)));
    }

    private static CrashRoundResponse ToResponse(CrashRound round) => new(
        round.Id, round.Phase.ToString(), round.Commitment, round.OpenedAt, round.BettingEndsAt, round.StartedAt, round.CrashedAt,
        round.CrashPoint, round.ServerSeed, round.EdgePermille, round.GrowthPerSecond);

    private static CrashBetResponse ToResponse(CrashBet bet) => new(
        bet.Id, bet.RoundId, bet.Status, bet.Stake, bet.AutoCashOut, bet.IsActive, bet.CashedOutAt, bet.Payout, bet.FailureReason, bet.PlacedAt);
}
