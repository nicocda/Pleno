using Casino.BuildingBlocks;
using Casino.Modules.Games.Application;
using Casino.Modules.Games.Fairness;
using Casino.Modules.Games.Roulette;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Casino.Modules.Games.Api;

/// <summary>Una apuesta de la tirada.</summary>
public sealed record BetLineBody(string BetType, int[]? Selection, long Stake);

/// <summary>
/// El jugador y su cuenta salen del token: el cuerpo solo dice QUE se apuesta. Una tirada lleva una o varias apuestas en
/// <c>Bets</c>; el formato de una sola apuesta (BetType, Selection, Stake) sigue funcionando.
/// </summary>
public sealed record PlaceBetBody(string? BetType, int[]? Selection, long Stake, BetLineBody[]? Bets = null);

public sealed record BetLineResponse(string BetType, IReadOnlyList<int> Selection, long Stake);

public sealed record RoundResponse(
    Guid BetId,
    RoundStatus Status,
    string BetType,
    IReadOnlyList<int> Selection,
    long Stake,
    IReadOnlyList<BetLineResponse> Bets,
    Guid PairId,
    long Nonce,
    int? WinningNumber,
    long? Payout,
    string? FailureReason,
    DateTimeOffset PlacedAt);

internal static class RouletteEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";
    private const int MaxHistory = 100;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/games/roulette").WithTags("Roulette");
        group.RequireAuthorization(policy => policy.RequireRole(Roles.Player));
        group.AddEndpointFilter(GamesEndpointFilters.MapDomainErrors);

        // La apuesta se resuelve de forma asincrona (Wallet reserva -> juego sortea -> Wallet liquida): responde 202
        // y la ronda se consulta por su BetId. El servidor asigna el nonce; el cliente no puede elegirlo.
        group.MapPost("/bets", async (PlaceBetBody body, HttpContext http, RouletteService roulette, CancellationToken ct) =>
        {
            var lines = new List<BetLine>();
            foreach (var line in body.Bets is { Length: > 0 } ? body.Bets : [new BetLineBody(body.BetType ?? string.Empty, body.Selection, body.Stake)])
            {
                if (!Enum.TryParse<RouletteBetType>(line.BetType, ignoreCase: true, out var betType) || !Enum.IsDefined(betType))
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: nameof(GamesError.InvalidBet),
                        detail: $"Tipo de apuesta desconocido: {line.BetType}.");
                }

                lines.Add(new BetLine(betType, line.Selection ?? [], line.Stake));
            }

            var userId = http.User.GetUserId();
            var placed = await roulette.PlaceSpinAsync(
                new PlaceRouletteSpinRequest(userId, PlayerIds.WalletAccountFor(userId), lines),
                http.Request.Headers[IdempotencyHeader].ToString(),
                ct);
            return Results.Accepted($"/games/roulette/rounds/{placed.BetId}", placed);
        });

        // Solo se ven las rondas propias. Una ajena responde 404 (no 403) para no revelar que existe.
        group.MapGet("/rounds/{betId:guid}", async (Guid betId, HttpContext http, RouletteService roulette, CancellationToken ct) =>
        {
            var round = await roulette.GetRoundAsync(betId, ct);
            if (round.UserId != http.User.GetUserId())
            {
                throw new GamesDomainException(GamesError.RoundNotFound, $"No existe la ronda {betId}.");
            }

            return Results.Ok(ToResponse(round));
        });

        group.MapGet("/rounds", async (HttpContext http, RouletteService roulette, int? limit, CancellationToken ct) =>
        {
            var rounds = await roulette.GetHistoryAsync(http.User.GetUserId(), Math.Clamp(limit ?? 20, 1, MaxHistory), ct);
            return Results.Ok(rounds.Select(ToResponse));
        });
    }

    private static RoundResponse ToResponse(RouletteRound round) => new(
        round.Id, round.Status, round.BetType.ToString(), round.Selection, round.Stake,
        [.. round.AllBets().Select(b => new BetLineResponse(b.BetType.ToString(), b.Selection, b.Stake))], round.PairId, round.Nonce,
        round.WinningNumber, round.Payout, round.FailureReason, round.PlacedAt);
}
