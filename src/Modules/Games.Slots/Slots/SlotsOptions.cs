using Microsoft.Extensions.Configuration;

namespace Casino.Modules.Games.Slots;

/// <summary>
/// La tabla de pagos tal como se escribe en la configuracion (seccion <c>Slots</c> de appsettings). Si la seccion no existe se usa
/// <see cref="SlotsPaytable.Default"/>. Al arrancar se valida: una tabla con retorno mayor al 100% impide iniciar la API.
/// </summary>
public sealed class SlotsOptions
{
    public List<SymbolOption> Symbols { get; set; } = [];

    public List<LeadingPayOption> LeadingPays { get; set; } = [];

    public long MaxStake { get; set; } = SlotsPaytable.Default.MaxStake;

    public static SlotsPaytable Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection("Slots");
        if (!section.Exists())
        {
            return SlotsPaytable.Default;
        }

        var options = section.Get<SlotsOptions>() ?? new SlotsOptions();
        return new SlotsPaytable(
            options.Symbols.Select(s => new SlotSymbol(s.Name, s.Weight, s.TriplePayout)),
            options.LeadingPays.Select(p => new LeadingPay(p.Symbol, p.Count, p.Payout)),
            options.MaxStake);
    }
}

public sealed class SymbolOption
{
    public string Name { get; set; } = string.Empty;

    public int Weight { get; set; }

    public long TriplePayout { get; set; }
}

public sealed class LeadingPayOption
{
    public string Symbol { get; set; } = string.Empty;

    public int Count { get; set; }

    public long Payout { get; set; }
}
