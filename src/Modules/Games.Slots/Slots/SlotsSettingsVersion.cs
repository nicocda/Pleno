namespace Casino.Modules.Games.Slots;

/// <summary>
/// Una version de la tabla de pagos guardada por un administrador. Solo se agregan versiones (nunca se editan ni se borran):
/// asi queda quien cambio que y cuando, y un giro viejo se puede recalcular con la tabla que tenia en ese momento.
/// La version 0 es la de la configuracion (appsettings) y no se guarda: es el punto de partida.
/// </summary>
public sealed class SlotsSettingsVersion
{
    public string Id { get; set; } = string.Empty;

    public int Version { get; set; }

    public List<SymbolOption> Symbols { get; set; } = [];

    public List<LeadingPayOption> LeadingPays { get; set; } = [];

    public long MaxStake { get; set; }

    /// <summary>Solo ids: quien lo cambio. Nunca nombre ni email.</summary>
    public Guid ChangedBy { get; set; }

    public DateTimeOffset ChangedAt { get; set; }

    /// <summary>Retorno y frecuencia de premio calculados al guardar, para el historial.</summary>
    public double ReturnToPlayerPercent { get; set; }

    public double HitRatePercent { get; set; }

    public static string BuildId(int version) => $"slots-settings:{version}";

    public SlotsPaytable ToPaytable() => new(
        Symbols.Select(s => new SlotSymbol(s.Name, s.Weight, s.TriplePayout)),
        LeadingPays.Select(p => new LeadingPay(p.Symbol, p.Count, p.Payout)),
        MaxStake);

    public static SlotsSettingsVersion From(int version, SlotsPaytable table, Guid changedBy, DateTimeOffset at) => new()
    {
        Id = BuildId(version),
        Version = version,
        Symbols = [.. table.Symbols.Select(s => new SymbolOption { Name = s.Name, Weight = s.Weight, TriplePayout = s.TriplePayout })],
        LeadingPays = [.. table.LeadingPays.Select(p => new LeadingPayOption { Symbol = p.Symbol, Count = p.Count, Payout = p.Payout })],
        MaxStake = table.MaxStake,
        ChangedBy = changedBy,
        ChangedAt = at,
        ReturnToPlayerPercent = Math.Round(table.ReturnToPlayerPercent, 2),
        HitRatePercent = Math.Round(table.HitRatePercent, 2),
    };
}
