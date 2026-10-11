using Casino.Modules.Games.Application;
using Marten.Schema;

namespace Casino.Modules.Games.Tables;

public enum TableStatus
{
    /// <summary>Esperando jugadores: se puede entrar, salir y agregar bots. El que la creo la inicia.</summary>
    Open = 1,

    /// <summary>Se esta jugando. Las fichas de cada asiento estan reservadas hasta que termine.</summary>
    Playing = 2,

    /// <summary>Termino: se pago y la semilla se revelo. Estado final.</summary>
    Finished = 3,

    /// <summary>Se cancelo (se fueron todos o nadie la inicio): se devolvieron las fichas. Estado final.</summary>
    Cancelled = 4,
}

/// <summary>Un asiento de la mesa: una persona (con su cuenta) o un bot.</summary>
public sealed class TableSeat
{
    public Guid? UserId { get; set; }

    public Guid? AccountId { get; set; }

    /// <summary>La apuesta (reserva en la Wallet) de este asiento. Null en los bots: la casa respalda lo que pone un bot.</summary>
    public Guid? BetId { get; set; }

    public bool IsBot { get; set; }

    /// <summary>Cuantos turnos seguidos dejo vencer. Con 3 el asiento queda "ausente" y lo juega un bot por el.</summary>
    public int Misses { get; set; }

    public bool Away { get; set; }
}

/// <summary>
/// Una mesa de un juego ENTRE JUGADORES. Guarda quienes estan sentados, el estado de la partida (que solo entiende el juego) y de quien es el turno. La semilla de la
/// partida se guarda cifrada y se revela al terminar; su compromiso (SHA-256) se publica desde que se crea la mesa, antes de que nadie se siente.
/// </summary>
public sealed class PlayerTable
{
    public Guid Id { get; set; }

    public string GameId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public Guid OwnerUserId { get; set; }

    /// <summary>Una mesa privada no aparece en el listado: se entra con el codigo.</summary>
    public bool IsPrivate { get; set; }

    public string? JoinCode { get; set; }

    /// <summary>Lo que pone cada asiento (en fichas enteras).</summary>
    public long BuyIn { get; set; }

    public int MaxPlayers { get; set; }

    public TableStatus Status { get; set; }

    public List<TableSeat> Seats { get; set; } = [];

    /// <summary>Los usuarios sentados (copia de <see cref="Seats"/> para poder buscar las mesas de alguien).</summary>
    public List<Guid> Humans { get; set; } = [];

    public string Commitment { get; set; } = string.Empty;

    public string EncryptedSeed { get; set; } = string.Empty;

    /// <summary>La semilla en claro: solo se guarda al revelarla, cuando la partida ya termino.</summary>
    public string? ServerSeed { get; set; }

    /// <summary>El estado de la partida, en el formato propio del juego. Null hasta que se inicia.</summary>
    public string? StateJson { get; set; }

    public int? TurnSeat { get; set; }

    public DateTimeOffset? TurnEndsAt { get; set; }

    /// <summary>Cuando el motor tiene que actuar solo: el bot juega o el turno vencio. Null si no hay nada que hacer solo.</summary>
    public DateTimeOffset? NextAutoAt { get; set; }

    /// <summary>Lo que se llevo cada asiento al terminar.</summary>
    public List<long>? Payouts { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    [Version]
    public Guid Version { get; set; }

    public bool IsFinal => Status is TableStatus.Finished or TableStatus.Cancelled;

    public int HumanCount => Seats.Count(s => !s.IsBot);

    public int SeatOf(Guid userId) => Seats.FindIndex(s => s.UserId == userId);

    /// <summary>Mantiene <see cref="Humans"/> al dia despues de cambiar los asientos.</summary>
    public void SyncHumans() => Humans = [.. Seats.Where(s => s.UserId is not null).Select(s => s.UserId!.Value)];
}

/// <summary>
/// La apuesta de un asiento humano en una mesa. Sigue el protocolo de rondas con la Wallet: reserva al sentarse, queda activa hasta que termina la partida y se
/// liquida con lo que se llevo. Cada transicion es idempotente: aplicar dos veces el mismo mensaje devuelve false.
/// </summary>
public sealed class TableBet
{
    /// <summary>Id de la apuesta (BetId): tambien es el id de la reserva en la Wallet.</summary>
    public Guid Id { get; set; }

    public Guid TableId { get; set; }

    public string GameId { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    public Guid AccountId { get; set; }

    public long Stake { get; set; }

    public bool Reserved { get; set; }

    public long? Payout { get; set; }

    public RoundStatus Status { get; set; }

    public string? FailureReason { get; set; }

    public DateTimeOffset PlacedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    [Version]
    public Guid Version { get; set; }

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

    /// <summary>Termino la partida: se cobra lo que se llevo el asiento (puede ser 0, lo apostado o mas).</summary>
    public bool Finish(long payout)
    {
        if (!IsActive)
        {
            return false;
        }

        Payout = payout;
        Status = RoundStatus.Resolved;
        return true;
    }

    /// <summary>La apuesta no participo (se fue antes de empezar, la mesa se cancelo, llego tarde): se devuelve lo apostado, ni mas ni menos.</summary>
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
