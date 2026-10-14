using Casino.Modules.Games.Application;
using Marten.Schema;

namespace Casino.Modules.Games.Dice;

/// <summary>
/// Una tirada de dados de punta a punta. Cada transicion es idempotente: aplicar dos veces el mismo mensaje (entrega "al menos una vez")
/// deja la ronda igual y devuelve false.
/// </summary>
public sealed class DiceRound
{
    /// <summary>Id de la apuesta (BetId): tambien es el id de la reserva en la Wallet.</summary>
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid AccountId { get; set; }

    public DiceDirection Direction { get; set; }

    /// <summary>Probabilidad de ganar elegida, en centesimas de punto porcentual (4950 = 49,50 %).</summary>
    public int Chance { get; set; }

    public long Stake { get; set; }

    public Guid PairId { get; set; }

    public long Nonce { get; set; }

    public RoundStatus Status { get; set; }

    /// <summary>El numero que salio (0 a 9999, o sea 0,00 a 99,99).</summary>
    public int? Roll { get; set; }

    public long? Payout { get; set; }

    public string? FailureReason { get; set; }

    public DateTimeOffset PlacedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    [Version]
    public Guid Version { get; set; }

    public bool IsClosed => Status is RoundStatus.Settled or RoundStatus.Rejected or RoundStatus.Voided;

    public DiceBet ToBet() => DiceBet.Create(Direction, Chance, Stake);

    public bool MarkResolved(int roll, long payout)
    {
        if (Status != RoundStatus.Placed)
        {
            return false;
        }

        Status = RoundStatus.Resolved;
        Roll = roll;
        Payout = payout;
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

    /// <summary>Anula la ronda. El resultado sorteado (si lo hubo) se conserva para auditoria pero no paga.</summary>
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
