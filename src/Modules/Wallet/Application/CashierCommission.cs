using Microsoft.Extensions.Configuration;

namespace Casino.Modules.Wallet.Application;

/// <summary>
/// Configuracion de la comision de los cajeros (seccion <c>Cashiers</c>). Los valores por defecto sirven; se cambian sin recompilar y se validan al arrancar.
/// La comision la <b>paga la casa</b> (emite fichas nuevas, acotadas por estos topes) y se acredita a quien hizo la carga.
/// </summary>
public sealed class CashierOptions
{
    /// <summary>Comision del cajero sobre lo que carga a un jugador, en milesimas (20 = 2 %).</summary>
    public int CashierCommissionPermille { get; set; } = 20;

    /// <summary>Comision del jefe de cajeros sobre lo que carga a un cajero, en milesimas (0 = no cobra).</summary>
    public int HeadCashierCommissionPermille { get; set; }

    /// <summary>Tope de la comision de una sola carga, en fichas.</summary>
    public long MaxCommissionPerLoad { get; set; } = 1_000;

    /// <summary>Tope de lo que una persona cobra de comision por dia (UTC), en fichas.</summary>
    public long MaxCommissionPerDay { get; set; } = 10_000;

    /// <summary>Retiro minimo, en fichas.</summary>
    public long MinWithdrawal { get; set; } = 10;

    /// <summary>Horas que un retiro puede esperar a que lo atiendan: despues la reserva se libera sola y las fichas vuelven al jugador.</summary>
    public int WithdrawalExpiryHours { get; set; } = 72;

    public static CashierOptions Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection("Cashiers").Get<CashierOptions>() ?? new CashierOptions();
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (CashierCommissionPermille is < 0 or > 200 || HeadCashierCommissionPermille is < 0 or > 200)
        {
            throw new InvalidOperationException("Cashiers: la comision va de 0 a 200 milesimas (0 a 20 %).");
        }

        if (MaxCommissionPerLoad < 0 || MaxCommissionPerDay < 0)
        {
            throw new InvalidOperationException("Cashiers: los topes de comision no pueden ser negativos.");
        }

        if (MinWithdrawal < 1 || WithdrawalExpiryHours is < 1 or > 24 * 30)
        {
            throw new InvalidOperationException("Cashiers: el retiro minimo es de al menos 1 ficha y el plazo va de 1 hora a 30 dias.");
        }
    }

    public int PermilleFor(HierarchyLevel level) => level switch
    {
        HierarchyLevel.Cashier => CashierCommissionPermille,
        HierarchyLevel.HeadCashier => HeadCashierCommissionPermille,
        _ => 0,
    };
}

/// <summary>La decision de comision de una carga, guardada para que un reintento pague exactamente lo mismo (y para auditar lo que la casa emitio).</summary>
public sealed class CommissionRecord
{
    /// <summary>Quien carga + la clave de idempotencia de la carga: una carga, una comision.</summary>
    public required string Id { get; init; }

    public required Guid ActorUserId { get; init; }

    public required Guid TargetUserId { get; init; }

    /// <summary>Lo que se cargo.</summary>
    public required long LoadedAmount { get; init; }

    /// <summary>Lo que la casa le pago a quien cargo (puede ser 0 si no cobra o se llego a un tope).</summary>
    public required long Amount { get; init; }

    public required Guid TransferTransactionId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public static string BuildId(Guid actorUserId, string loadKey) => $"{actorUserId:N}:{loadKey}";
}

public static class CashierCommissionMath
{
    /// <summary>
    /// La comision de una carga: el porcentaje del monto (hacia abajo, entero), sin pasar el tope por carga ni lo que le queda del tope diario. Nunca negativa.
    /// </summary>
    public static long Compute(long amount, int permille, long perLoadCap, long remainingToday)
    {
        if (amount <= 0 || permille <= 0)
        {
            return 0;
        }

        var cap = Math.Max(0, Math.Min(perLoadCap, remainingToday));
        // amount * permille / 1000 sin desbordar: si el monto es enorme, igual manda el tope.
        var raw = amount > long.MaxValue / permille ? long.MaxValue : amount * permille / 1000;
        return Math.Min(raw, cap);
    }
}
