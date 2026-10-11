using Marten.Schema;

namespace Casino.Modules.Games.Blackjack;

public enum BlackjackPhase
{
    /// <summary>La mano esta abierta para apostar. Hasta la primera apuesta no corre ningun reloj (<see cref="BlackjackRound.BettingEndsAt"/> es null).</summary>
    Betting = 1,

    /// <summary>Se repartio: los jugadores juegan de a uno y despues juega el crupier.</summary>
    Playing = 2,

    /// <summary>La mano termino, se pago y la semilla se revelo. Estado final.</summary>
    Finished = 3,

    /// <summary>La mano se corto (el servicio se reinicio): las apuestas se devolvieron. Estado final.</summary>
    Aborted = 4,
}

/// <summary>
/// Una mano de una mesa de Blackjack. La semilla del zapato se guarda cifrada y recien se revela al terminar; su compromiso (SHA-256) se publica antes
/// de aceptar apuestas. Por eso la carta tapada del crupier NO se guarda: se calcula del zapato recien cuando hay que mostrarla.
/// </summary>
public sealed class BlackjackRound
{
    public Guid Id { get; set; }

    public string TableId { get; set; } = string.Empty;

    public BlackjackPhase Phase { get; set; }

    public string Commitment { get; set; } = string.Empty;

    public string EncryptedSeed { get; set; } = string.Empty;

    public DateTimeOffset OpenedAt { get; set; }

    /// <summary>Cuando se cierra la ventana de apuestas. Null mientras nadie apuesto: la primera apuesta pone en marcha el reloj.</summary>
    public DateTimeOffset? BettingEndsAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Cuantos jugadores se sentaron en el reparto.</summary>
    public int SeatCount { get; set; }

    /// <summary>Las cartas del crupier que ya se pueden ver. La tapada no esta hasta que <see cref="DealerRevealed"/>.</summary>
    public List<int> DealerCards { get; set; } = [];

    public bool DealerRevealed { get; set; }

    /// <summary>La proxima carta del zapato que se va a repartir.</summary>
    public int NextCard { get; set; }

    /// <summary>La apuesta a la que le toca jugar y hasta cuando (null si no hay turno en curso).</summary>
    public Guid? ActiveBetId { get; set; }

    public DateTimeOffset? TurnEndsAt { get; set; }

    /// <summary>La semilla en claro: solo se guarda al revelarla, cuando la mano ya termino.</summary>
    public string? ServerSeed { get; set; }

    [Version]
    public Guid Version { get; set; }

    public bool IsFinal => Phase is BlackjackPhase.Finished or BlackjackPhase.Aborted;

    /// <summary>La primera apuesta pone en marcha la ventana de apuestas. Devuelve false si ya estaba en marcha.</summary>
    public bool StartBettingClock(DateTimeOffset now, double seconds)
    {
        if (Phase != BlackjackPhase.Betting || BettingEndsAt is not null)
        {
            return false;
        }

        BettingEndsAt = now.AddSeconds(seconds);
        return true;
    }

    /// <summary>Nadie pudo jugar: la mano vuelve a esperar la primera apuesta.</summary>
    public bool BackToWaiting()
    {
        if (Phase != BlackjackPhase.Betting || BettingEndsAt is null)
        {
            return false;
        }

        BettingEndsAt = null;
        return true;
    }

    public bool BeginPlaying(int seats, int dealerUpCard, int nextCard)
    {
        if (Phase != BlackjackPhase.Betting)
        {
            return false;
        }

        Phase = BlackjackPhase.Playing;
        SeatCount = seats;
        DealerCards = [dealerUpCard];
        NextCard = nextCard;
        return true;
    }

    public bool SetTurn(Guid betId, DateTimeOffset endsAt)
    {
        if (Phase != BlackjackPhase.Playing)
        {
            return false;
        }

        ActiveBetId = betId;
        TurnEndsAt = endsAt;
        return true;
    }

    public void ClearTurn()
    {
        ActiveBetId = null;
        TurnEndsAt = null;
    }

    public bool RevealHole(int holeCard)
    {
        if (Phase != BlackjackPhase.Playing || DealerRevealed)
        {
            return false;
        }

        DealerCards.Add(holeCard);
        DealerRevealed = true;
        ClearTurn();
        return true;
    }

    public bool DealerDraws(int card)
    {
        if (Phase != BlackjackPhase.Playing || !DealerRevealed)
        {
            return false;
        }

        DealerCards.Add(card);
        NextCard++;
        return true;
    }

    public bool Finish(DateTimeOffset now, string revealedSeed)
    {
        if (Phase != BlackjackPhase.Playing)
        {
            return false;
        }

        Phase = BlackjackPhase.Finished;
        FinishedAt = now;
        ServerSeed = revealedSeed;
        ClearTurn();
        return true;
    }

    public bool MarkAborted(DateTimeOffset now)
    {
        if (IsFinal)
        {
            return false;
        }

        Phase = BlackjackPhase.Aborted;
        FinishedAt = now;
        ClearTurn();
        return true;
    }
}
