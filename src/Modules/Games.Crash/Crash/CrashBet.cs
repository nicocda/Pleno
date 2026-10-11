using Casino.Modules.Games.Application;
using Marten.Schema;

namespace Casino.Modules.Games.Crash;

/// <summary>
/// La apuesta de un jugador en una ronda de Crash. Sigue el protocolo de rondas con la Wallet pero con un paso mas: queda "activa" (reservada)
/// hasta que el jugador retira o la ronda explota. Cada transicion es idempotente: aplicar dos veces el mismo mensaje devuelve false.
/// </summary>
public sealed class CrashBet
{
    /// <summary>Id de la apuesta (BetId): tambien es el id de la reserva en la Wallet.</summary>
    public Guid Id { get; set; }

    public Guid RoundId { get; set; }

    public Guid UserId { get; set; }

    public Guid AccountId { get; set; }

    public long Stake { get; set; }

    /// <summary>Si el jugador pidio retirar solo al llegar a este multiplicador (en centesimas).</summary>
    public long? AutoCashOut { get; set; }

    /// <summary>La Wallet ya reservo las fichas: la apuesta esta en juego.</summary>
    public bool Reserved { get; set; }

    /// <summary>El multiplicador (en centesimas) en el que se retiro, si se retiro antes de explotar.</summary>
    public long? CashedOutAt { get; set; }

    public long? Payout { get; set; }

    public RoundStatus Status { get; set; }

    public string? FailureReason { get; set; }

    public DateTimeOffset PlacedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    [Version]
    public Guid Version { get; set; }

    public bool IsClosed => Status is RoundStatus.Settled or RoundStatus.Rejected or RoundStatus.Voided;

    /// <summary>La apuesta esta en juego: reservada y todavia sin resultado.</summary>
    public bool IsActive => Status == RoundStatus.Placed && Reserved;

    public bool MarkReserved()
    {
        if (Status != RoundStatus.Placed || Reserved)
        {
            return false;
        }

        Reserved = true;
        return true;
    }

    /// <summary>El jugador retira en <paramref name="multiplier"/> antes de que explote.</summary>
    public bool CashOut(long multiplier, long payout)
    {
        if (!IsActive)
        {
            return false;
        }

        CashedOutAt = multiplier;
        Payout = payout;
        Status = RoundStatus.Resolved;
        return true;
    }

    /// <summary>La ronda exploto sin que retire: pierde lo apostado.</summary>
    public bool Lose()
    {
        if (!IsActive)
        {
            return false;
        }

        Payout = 0;
        Status = RoundStatus.Resolved;
        return true;
    }

    /// <summary>La apuesta no pudo participar (llego tarde o la ronda se corto): se devuelve lo apostado, ni mas ni menos.</summary>
    public bool Refund(string reason)
    {
        if (Status != RoundStatus.Placed)
        {
            return false;
        }

        Reserved = true;
        Payout = Stake;
        FailureReason = reason;
        Status = RoundStatus.Resolved;
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
        if (Status != RoundStatus.Placed || Reserved)
        {
            return false;
        }

        Status = RoundStatus.Rejected;
        FailureReason = reason;
        ClosedAt = now;
        return true;
    }

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
