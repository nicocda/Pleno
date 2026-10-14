using Casino.BuildingBlocks;
using Casino.Modules.Games.Api;
using Casino.Modules.Games.Application;
using Casino.Modules.Games.Dice.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Casino.Modules.Games.Dice.Api;

/// <summary>El jugador y su cuenta salen del token: el cuerpo solo dice QUE se apuesta.</summary>
public sealed record PlaceDiceBody(string? Direction, int Chance, long Stake);

public sealed record DiceRoundResponse(
    Guid BetId,
    RoundStatus Status,
    string Direction,
    int Chance,
    long Stake,
    Guid PairId,
    long Nonce,
    int? Roll,
    long? Payout,
    string? FailureReason,
    DateTimeOffset PlacedAt);

internal static class DiceEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";
    private const int MaxHistory = 100;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/games/dice").WithTags("Dice");
        group.RequireAuthorization(policy => policy.RequireRole(Roles.Player));
        group.AddEndpointFilter(GamesEndpointFilters.MapDomainErrors);

        // La apuesta se resuelve de forma asincrona (Wallet reserva -> juego sortea -> Wallet liquida): responde 202 y la ronda se consulta por su BetId.
        group.MapPost("/bets", async (PlaceDiceBody body, HttpContext http, DiceService dice, CancellationToken ct) =>
        {
            if (!Enum.TryParse<DiceDirection>(body.Direction, ignoreCase: true, out var direction) || !Enum.IsDefined(direction))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: nameof(GamesError.InvalidBet),
                    detail: $"Direccion desconocida: {body.Direction}. Usa Under u Over.");
            }

            var userId = http.User.GetUserId();
            var placed = await dice.PlaceBetAsync(
                new PlaceDiceBetRequest(userId, PlayerIds.WalletAccountFor(userId), direction, body.Chance, body.Stake),
                http.Request.Headers[IdempotencyHeader].ToString(),
                ct);
            return Results.Accepted($"/games/dice/rounds/{placed.BetId}", placed);
        });

        // Solo se ven las rondas propias. Una ajena responde 404 (no 403) para no revelar que existe.
        group.MapGet("/rounds/{betId:guid}", async (Guid betId, HttpContext http, DiceService dice, CancellationToken ct) =>
        {
            var round = await dice.GetRoundAsync(betId, ct);
            if (round.UserId != http.User.GetUserId())
            {
                throw new GamesDomainException(GamesError.RoundNotFound, $"No existe la ronda {betId}.");
            }

            return Results.Ok(ToResponse(round));
        });

        group.MapGet("/rounds", async (HttpContext http, DiceService dice, int? limit, CancellationToken ct) =>
        {
            var rounds = await dice.GetHistoryAsync(http.User.GetUserId(), Math.Clamp(limit ?? 20, 1, MaxHistory), ct);
            return Results.Ok(rounds.Select(ToResponse));
        });
    }

    private static DiceRoundResponse ToResponse(DiceRound round) => new(
        round.Id, round.Status, round.Direction.ToString(), round.Chance, round.Stake, round.PairId, round.Nonce,
        round.Roll, round.Payout, round.FailureReason, round.PlacedAt);
}
