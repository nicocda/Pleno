using Casino.Modules.Games.Application;
using Marten.Schema;

namespace Casino.Modules.Games.Roulette;

/// <summary>Una apuesta dentro de una tirada (el tipo, lo que se eligio y las fichas puestas ahi).</summary>
public sealed class RoundBet
{
    public RouletteBetType BetType { get; set; }

    public List<int> Selection { get; set; } = [];

    public long Stake { get; set; }
}

/// <summary>
/// Una tirada de ruleta de punta a punta: UNA o varias apuestas que comparten un nonce, un numero sorteado y una reserva en la Wallet. Cada transicion es idempotente: aplicar dos veces el mismo mensaje
/// (entrega "al menos una vez") deja la ronda igual y devuelve false.
/// </summary>
public sealed class RouletteRound
{
    /// <summary>Id de la apuesta (BetId): tambien es el id de la reserva en la Wallet.</summary>
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid AccountId { get; set; }

    /// <summary>Tipo de la primera apuesta (se conserva por compatibilidad; el detalle completo esta en <see cref="Bets"/>).</summary>
    public RouletteBetType BetType { get; set; }

    /// <summary>Seleccion de la primera apuesta (ver <see cref="BetType"/>).</summary>
    public List<int> Selection { get; set; } = [];

    /// <summary>Total de fichas de la tirada: es lo que reserva la Wallet.</summary>
    public long Stake { get; set; }

    /// <summary>Todas las apuestas de la tirada. Vacia en rondas viejas, de una sola apuesta: ahi vale (BetType, Selection, Stake).</summary>
    public List<RoundBet> Bets { get; set; } = [];

    /// <summary>Par de seeds y nonce asignados por el servidor al colocar la apuesta (antes de conocer el resultado).</summary>
    public Guid PairId { get; set; }

    public long Nonce { get; set; }

    public RoundStatus Status { get; set; }

    public int? WinningNumber { get; set; }

    public long? Payout { get; set; }

    public string? FailureReason { get; set; }

    public DateTimeOffset PlacedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>Control de concurrencia optimista: dos manejadores simultaneos no pueden pisarse.</summary>
    [Version]
    public Guid Version { get; set; }

    public bool IsClosed => Status is RoundStatus.Settled or RoundStatus.Rejected or RoundStatus.Voided;

    /// <summary>Las apuestas de la tirada, tambien para las rondas viejas que guardaron una sola.</summary>
    public IReadOnlyList<RoundBet> AllBets() =>
        Bets.Count > 0 ? Bets : [new RoundBet { BetType = BetType, Selection = Selection, Stake = Stake }];

    public IReadOnlyList<RouletteBet> ToBets() => [.. AllBets().Select(b => RouletteBet.Create(b.BetType, b.Selection, b.Stake))];

    public bool MarkResolved(int winningNumber, long payout)
    {
        if (Status != RoundStatus.Placed)
        {
            return false;
        }

        Status = RoundStatus.Resolved;
        WinningNumber = winningNumber;
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
