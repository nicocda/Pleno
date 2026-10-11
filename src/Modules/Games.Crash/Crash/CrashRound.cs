using Marten.Schema;

namespace Casino.Modules.Games.Crash;

public enum CrashPhase
{
    /// <summary>Se aceptan apuestas. El compromiso (hash de la semilla) ya es publico.</summary>
    Betting = 1,

    /// <summary>El multiplicador esta subiendo. Las apuestas retiran o esperan.</summary>
    Running = 2,

    /// <summary>Exploto: la semilla se revelo y las apuestas sin retirar se perdieron. Estado final.</summary>
    Crashed = 3,

    /// <summary>La ronda se corto sin explotar (el servicio se reinicio): las apuestas se devolvieron. Estado final.</summary>
    Aborted = 4,
}

/// <summary>
/// Una ronda de Crash, COMPARTIDA por todos los jugadores. La semilla del servidor se guarda cifrada y recien se revela al explotar; antes de
/// aceptar apuestas se publica su compromiso (SHA-256), asi nadie puede cambiar el resultado despues de ver las apuestas.
/// </summary>
public sealed class CrashRound
{
    public Guid Id { get; set; }

    public CrashPhase Phase { get; set; }

    /// <summary>SHA-256 (hex) de la server seed: se publica al abrir la ronda.</summary>
    public string Commitment { get; set; } = string.Empty;

    /// <summary>La server seed cifrada (AES-GCM) con la clave maestra. Ni siquiera quien lea la base puede conocer el punto de explosion antes de tiempo.</summary>
    public string EncryptedSeed { get; set; } = string.Empty;

    public DateTimeOffset OpenedAt { get; set; }

    public DateTimeOffset BettingEndsAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CrashedAt { get; set; }

    /// <summary>Solo conocido (y publico) una vez que exploto.</summary>
    public long? CrashPoint { get; set; }

    /// <summary>La server seed en claro: solo se guarda al revelarla, cuando la ronda ya termino.</summary>
    public string? ServerSeed { get; set; }

    /// <summary>Parametros con los que se corrio la ronda, para poder verificarla aunque la configuracion cambie despues.</summary>
    public int EdgePermille { get; set; }

    public double GrowthPerSecond { get; set; }

    [Version]
    public Guid Version { get; set; }

    public bool IsFinal => Phase is CrashPhase.Crashed or CrashPhase.Aborted;

    public bool StartRunning(DateTimeOffset now)
    {
        if (Phase != CrashPhase.Betting)
        {
            return false;
        }

        Phase = CrashPhase.Running;
        StartedAt = now;
        return true;
    }

    public bool MarkCrashed(DateTimeOffset now, long crashPoint, string revealedSeed)
    {
        if (Phase != CrashPhase.Running)
        {
            return false;
        }

        Phase = CrashPhase.Crashed;
        CrashedAt = now;
        CrashPoint = crashPoint;
        ServerSeed = revealedSeed;
        return true;
    }

    public bool MarkAborted(DateTimeOffset now)
    {
        if (IsFinal)
        {
            return false;
        }

        Phase = CrashPhase.Aborted;
        CrashedAt = now;
        return true;
    }
}
