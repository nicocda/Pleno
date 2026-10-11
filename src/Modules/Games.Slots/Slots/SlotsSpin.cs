using Casino.Modules.Games.Application;
using Marten.Schema;

namespace Casino.Modules.Games.Slots;

/// <summary>
/// Un giro de tragamonedas de punta a punta. Usa los mismos estados que la ruleta (<see cref="RoundStatus"/>) y cada transicion
/// es idempotente: aplicar dos veces el mismo mensaje (entrega "al menos una vez") deja el giro igual y devuelve false.
/// </summary>
public sealed class SlotsSpin
{
    /// <summary>Id de la apuesta (BetId): tambien es el id de la reserva en la Wallet.</summary>
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid AccountId { get; set; }

    public long Stake { get; set; }

    /// <summary>Par de seeds y nonce asignados por el servidor al colocar la apuesta (antes de conocer el resultado).</summary>
    public Guid PairId { get; set; }

    public long Nonce { get; set; }

    public RoundStatus Status { get; set; }

    /// <summary>Simbolo de cada rodillo (por nombre, para que el historial sobreviva a cambios de la tabla).</summary>
    public List<string> Reels { get; set; } = [];

    /// <summary>Todos los pasos del giro (el primero es el giro inicial, los demas las cascadas). Vacio en giros anteriores a las cascadas.</summary>
    public List<SlotsStep> Steps { get; set; } = [];

    /// <summary>Version de la tabla de pagos con la que se sorteo (0 = la de la configuracion). Permite recalcular el giro mas adelante.</summary>
    public int? PaytableVersion { get; set; }

    public long? Multiplier { get; set; }

    public long? Payout { get; set; }

    public string? FailureReason { get; set; }

    public DateTimeOffset PlacedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>Control de concurrencia optimista: dos manejadores simultaneos no pueden pisarse.</summary>
    [Version]
    public Guid Version { get; set; }

    public bool IsClosed => Status is RoundStatus.Settled or RoundStatus.Rejected or RoundStatus.Voided;

    public bool MarkResolved(IEnumerable<string> reels, long multiplier, long payout, int paytableVersion = 0, IEnumerable<SlotsStep>? steps = null)
    {
        if (Status != RoundStatus.Placed)
        {
            return false;
        }

        Status = RoundStatus.Resolved;
        Reels = [.. reels];
        Steps = [.. steps ?? []];
        Multiplier = multiplier;
        Payout = payout;
        PaytableVersion = paytableVersion;
        return true;
    }

    public bool MarkSettled(DateTimeOffset now)
    {
        if (Status != RoundStatus.Resolved)
        {
            return false;
        }

        Status = RoundStatus.Settled;
        ClosedAt = now;
        return true;
    }

    public bool MarkRejected(string reason, DateTimeOffset now)
    {
        if (Status != RoundStatus.Placed)
        {
            return false;
        }

        Status = RoundStatus.Rejected;
        FailureReason = reason;
        ClosedAt = now;
        return true;
    }

    /// <summary>Anula el giro. El resultado sorteado (si lo hubo) se conserva para auditoria pero no paga.</summary>
    public bool MarkVoided(string reason, DateTimeOffset now)
    {
        if (Status is not (RoundStatus.Placed or RoundStatus.Resolved))
        {
            return false;
        }

        Status = RoundStatus.Voided;
        FailureReason = reason;
        ClosedAt = now;
        return true;
    }
}
