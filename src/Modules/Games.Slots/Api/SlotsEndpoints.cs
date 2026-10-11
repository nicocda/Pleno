using Casino.BuildingBlocks;
using Casino.Modules.Games.Application;
using Casino.Modules.Games.Slots;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Casino.Modules.Games.Api;

/// <summary>El jugador y su cuenta salen del token: el cuerpo solo dice cuanto se apuesta.</summary>
public sealed record SpinBody(long Stake);

public sealed record SpinResponse(
    Guid BetId,
    RoundStatus Status,
    long Stake,
    IReadOnlyList<string> Reels,
    IReadOnlyList<SlotsStep> Steps,
    long? Multiplier,
    long? Payout,
    Guid PairId,
    long Nonce,
    string? FailureReason,
    DateTimeOffset PlacedAt,
    int? PaytableVersion);

public sealed record PaytableResponse(
    int Reels,
    IReadOnlyList<SlotSymbol> Symbols,
    IReadOnlyList<LeadingPay> LeadingPays,
    long MinStake,
    long MaxStake,
    int TotalWeight,
    double ReturnToPlayerPercent,
    double HitRatePercent,
    IReadOnlyList<int> CascadeMultipliers,
    long CascadeMinPay);

internal static class SlotsEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";
    private const int MaxHistory = 100;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/games/slots").WithTags("Slots");
        group.RequireAuthorization(policy => policy.RequireRole(Roles.Player));
        group.AddEndpointFilter(GamesEndpointFilters.MapDomainErrors);

        // Tabla de pagos y retorno: informacion publica para el jugador.
        group.MapGet("/paytable", async (SlotsService slots, CancellationToken ct) =>
        {
            var table = (await slots.GetSettingsAsync(ct)).Paytable;
            return Results.Ok(new PaytableResponse(
                SlotsPaytable.ReelCount, table.Symbols, table.LeadingPays, table.MinStake, table.MaxStake,
                table.TotalWeight, Math.Round(table.ReturnToPlayerPercent, 2), Math.Round(table.HitRatePercent, 2),
                SlotsPaytable.CascadeMultipliers, SlotsPaytable.CascadeMinPay));
        });

        // Como en la ruleta: responde 202 y el giro se consulta por su BetId. El servidor asigna el nonce.
        group.MapPost("/spins", async (SpinBody body, HttpContext http, SlotsService slots, CancellationToken ct) =>
        {
            var userId = http.User.GetUserId();
            var placed = await slots.PlaceSpinAsync(userId, PlayerIds.WalletAccountFor(userId), body.Stake, http.Request.Headers[IdempotencyHeader].ToString(), ct);
            return Results.Accepted($"/games/slots/spins/{placed.BetId}", placed);
        });

        // Solo se ven los giros propios. Uno ajeno responde 404 (no 403) para no revelar que existe.
        group.MapGet("/spins/{betId:guid}", async (Guid betId, HttpContext http, SlotsService slots, CancellationToken ct) =>
        {
            var spin = await slots.GetSpinAsync(betId, ct);
            if (spin.UserId != http.User.GetUserId())
            {
                throw new GamesDomainException(GamesError.RoundNotFound, $"No existe el giro {betId}.");
            }

            return Results.Ok(ToResponse(spin));
        });

        group.MapGet("/spins", async (HttpContext http, SlotsService slots, int? limit, CancellationToken ct) =>
        {
            var spins = await slots.GetHistoryAsync(http.User.GetUserId(), Math.Clamp(limit ?? 20, 1, MaxHistory), ct);
            return Results.Ok(spins.Select(ToResponse));
        });
    }

    private static SpinResponse ToResponse(SlotsSpin spin) => new(
        spin.Id, spin.Status, spin.Stake, spin.Reels, spin.Steps, spin.Multiplier, spin.Payout, spin.PairId, spin.Nonce, spin.FailureReason, spin.PlacedAt, spin.PaytableVersion);
}
