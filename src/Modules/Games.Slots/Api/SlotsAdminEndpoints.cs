using Casino.BuildingBlocks;
using Casino.Modules.Games.Application;
using Casino.Modules.Games.Slots;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Casino.Modules.Games.Api;

public sealed record SymbolBody(string Name, int Weight, long TriplePayout);

public sealed record LeadingPayBody(string Symbol, int Count, long Payout);

/// <summary><paramref name="BaseVersion"/> es la version que el administrador estaba viendo (para detectar que otro publico antes).</summary>
public sealed record SettingsBody(SymbolBody[]? Symbols, LeadingPayBody[]? LeadingPays, long MaxStake, int BaseVersion = 0);

public sealed record SettingsView(
    IReadOnlyList<SlotSymbol> Symbols,
    IReadOnlyList<LeadingPay> LeadingPays,
    long MaxStake,
    int TotalWeight,
    double ReturnToPlayerPercent,
    double HitRatePercent);

public sealed record SettingsResponse(int Version, SettingsView Current, SettingsView Baseline);

public sealed record SettingsPreview(bool Valid, string? Error, double? ReturnToPlayerPercent, double? HitRatePercent, int? TotalWeight);

public sealed record SettingsHistoryItem(
    int Version, Guid ChangedBy, DateTimeOffset ChangedAt, double ReturnToPlayerPercent, double HitRatePercent, long MaxStake, int Symbols);

/// <summary>Ajustes de la tragamonedas para el panel de administracion: ver, probar (sin guardar), publicar y revisar el historial.</summary>
internal static class SlotsAdminEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/backoffice/games/slots").WithTags("Backoffice");
        group.RequireAuthorization(policy => policy.RequireRole(Roles.Backoffice));
        group.AddEndpointFilter(GamesEndpointFilters.MapDomainErrors);

        group.MapGet("/settings", async (SlotsSettingsStore settings, CancellationToken ct) =>
            Results.Ok(ToResponse(await settings.GetCurrentAsync(ct), settings.Baseline)));

        // Prueba una tabla SIN guardarla: el panel muestra el retorno y la frecuencia de premio mientras se edita.
        group.MapPost("/settings/preview", (SettingsBody body) =>
        {
            try
            {
                var table = Build(body);
                return Results.Ok(new SettingsPreview(true, null, Round(table.ReturnToPlayerPercent), Round(table.HitRatePercent), table.TotalWeight));
            }
            catch (GamesDomainException ex)
            {
                return Results.Ok(new SettingsPreview(false, ex.Message, null, null, null));
            }
        });

        group.MapPost("/settings", async (SettingsBody body, HttpContext http, SlotsSettingsStore settings, CancellationToken ct) =>
        {
            var saved = await settings.SaveAsync(http.User.GetUserId(), body.BaseVersion, Build(body), ct);
            return Results.Ok(ToResponse(saved, settings.Baseline));
        });

        group.MapGet("/settings/history", async (int? limit, SlotsSettingsStore settings, CancellationToken ct) =>
        {
            var versions = await settings.GetHistoryAsync(Math.Clamp(limit ?? 25, 1, 100), ct);
            return Results.Ok(versions.Select(v => new SettingsHistoryItem(
                v.Version, v.ChangedBy, v.ChangedAt, v.ReturnToPlayerPercent, v.HitRatePercent, v.MaxStake, v.Symbols.Count)));
        });
    }

    /// <summary>Arma la tabla con las mismas reglas que al arrancar. Cualquier incumplimiento sale como una apuesta invalida (400) con el motivo.</summary>
    private static SlotsPaytable Build(SettingsBody body)
    {
        try
        {
            return new SlotsPaytable(
                (body.Symbols ?? []).Select(s => new SlotSymbol(s.Name ?? string.Empty, s.Weight, s.TriplePayout)),
                (body.LeadingPays ?? []).Select(p => new LeadingPay(p.Symbol ?? string.Empty, p.Count, p.Payout)),
                body.MaxStake);
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            throw new GamesDomainException(GamesError.InvalidSettings, ex is OverflowException ? "Los numeros son demasiado grandes." : ex.Message.Split('\n')[0]);
        }
    }

    private static SettingsResponse ToResponse(SlotsSettingsSnapshot snapshot, SlotsPaytable baseline) =>
        new(snapshot.Version, ViewOf(snapshot.Paytable), ViewOf(baseline));

    private static SettingsView ViewOf(SlotsPaytable table) =>
        new(table.Symbols, table.LeadingPays, table.MaxStake, table.TotalWeight, Round(table.ReturnToPlayerPercent), Round(table.HitRatePercent));

    private static double Round(double value) => Math.Round(value, 2);
}
