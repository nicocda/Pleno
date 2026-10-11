using Casino.Modules.Games.Application;
using Marten.Schema;

namespace Casino.Modules.Games.Blackjack;

/// <summary>En que va la mano de un jugador.</summary>
public enum HandState
{
    /// <summary>Todavia no se repartio.</summary>
    Waiting = 0,

    /// <summary>Le toca decidir (o todavia no le llego el turno).</summary>
    Playing = 1,

    /// <summary>Se planto (a mano, por tiempo o porque llego a 21).</summary>
    Stood = 2,

    Bust = 3,

    /// <summary>Blackjack natural: no juega.</summary>
    Blackjack = 4,
}

/// <summary>
/// La apuesta de un jugador en una mano (sentarse en la mesa ES apostar). Sigue el protocolo de rondas con la Wallet y queda "activa" (reservada)
/// hasta que termina la mano. Cada transicion es idempotente: aplicar dos veces el mismo mensaje devuelve false.
/// </summary>
public sealed class BlackjackBet
{
    /// <summary>Id de la apuesta (BetId): tambien es el id de la reserva en la Wallet.</summary>
    public Guid Id { get; set; }

    public Guid RoundId { get; set; }

    public string TableId { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    public Guid AccountId { get; set; }

    public long Stake { get; set; }

    /// <summary>Numero de asiento (desde 1) una vez repartido; 0 antes.</summary>
    public int Seat { get; set; }

    public List<int> Cards { get; set; } = [];

    public HandState Hand { get; set; }

    public HandResult? Result { get; set; }

    public bool Reserved { get; set; }

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

    /// <summary>Se reparte: las dos primeras cartas, y el estado inicial (blackjack natural o a jugar).</summary>
    public bool Deal(int seat, int first, int second)
    {
        if (!IsActive || Hand != HandState.Waiting)
        {
            return false;
        }

        Seat = seat;
        Cards = [first, second];
        Hand = BlackjackMath.IsBlackjack(Cards) ? HandState.Blackjack : HandState.Playing;
        return true;
    }

    /// <summary>Pide una carta. Si se pasa pierde la mano; si llega a 21 se planta sola.</summary>
    public bool Hit(int card)
    {
        if (Hand != HandState.Playing)
        {
            return false;
        }

        Cards.Add(card);
        var total = BlackjackMath.Evaluate(Cards).Total;
        Hand = total > BlackjackMath.Twenty1 ? HandState.Bust : total == BlackjackMath.Twenty1 ? HandState.Stood : HandState.Playing;
        return true;
    }

    public bool Stand()
    {
        if (Hand != HandState.Playing)
        {
            return false;
        }

        Hand = HandState.Stood;
        return true;
    }

    /// <summary>La mano termino: se calcula el resultado contra el crupier y lo que se cobra.</summary>
    public bool Finish(HandResult result)
    {
        if (!IsActive)
        {
            return false;
        }

        Result = result;
        Payout = BlackjackMath.PayoutFor(Stake, result);
        Status = RoundStatus.Resolved;
        return true;
    }

    /// <summary>La apuesta no pudo participar (llego tarde, la mesa estaba llena o la mano se corto): se devuelve lo apostado, ni mas ni menos.</summary>
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
