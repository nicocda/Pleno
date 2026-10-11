using System.Security.Cryptography;
using System.Text;

namespace Casino.Modules.Games.Crash;

/// <summary>
/// Las matematicas de Crash, puras y deterministas. Los multiplicadores son enteros en CENTESIMAS (100 = x1,00; 250 = x2,50) y los pagos tambien
/// son enteros: nunca hay decimales en las fichas. El algoritmo esta especificado en docs/juego-crash.md para poder verificarlo.
/// </summary>
public static class CrashMath
{
    /// <summary>x1,00 en centesimas.</summary>
    public const long OneX = 100;

    /// <summary>Tope del punto de explosion: x100,00. Acota el pago maximo y la duracion de una ronda.</summary>
    public const long MaxMultiplier = 10_000;

    /// <summary>Ventaja de la casa por defecto, en milesimas: 30 = 3 %. Un jugador que siempre retira en el mismo multiplicador recupera ~97 %.</summary>
    public const int DefaultEdgePermille = 30;

    /// <summary>Velocidad con la que crece el multiplicador: x(t) = e^(0,07 t). A los 10 s va por x2; a los 60 s por x66.</summary>
    public const double DefaultGrowthPerSecond = 0.07;

    private const int MaxEdgePermille = 200;

    /// <summary>
    /// El punto en el que explota la ronda. Es una funcion de la server seed y del id de la ronda: HMAC-SHA256 (clave = la seed en UTF-8, mensaje
    /// "crash:{roundId sin guiones}"), se toman 52 bits (r) y el punto es piso(100 * (1000 - ventaja) * 2^52 / (1000 * (2^52 - r))), con minimo x1,00
    /// (explosion instantanea, con probabilidad igual a la ventaja) y tope x100.
    /// </summary>
    public static long CrashPoint(string serverSeed, Guid roundId, int edgePermille = DefaultEdgePermille)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverSeed);
        ArgumentOutOfRangeException.ThrowIfNegative(edgePermille);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(edgePermille, MaxEdgePermille);

        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(serverSeed), Encoding.UTF8.GetBytes($"crash:{roundId:N}"));
        ulong top56 = 0;
        for (var i = 0; i < 7; i++)
        {
            top56 = (top56 << 8) | mac[i];
        }

        var r = (UInt128)(top56 >> 4); // 52 bits: uniforme en [0, 2^52)
        var scale = (UInt128)1 << 52;
        var numerator = (UInt128)100 * (UInt128)(1000 - edgePermille) * scale;
        var denominator = (UInt128)1000 * (scale - r);
        var raw = numerator / denominator;

        return raw < (UInt128)OneX ? OneX : raw > (UInt128)MaxMultiplier ? MaxMultiplier : (long)raw;
    }

    /// <summary>El multiplicador (en centesimas) a los <paramref name="elapsedSeconds"/> de empezar a subir. Nunca baja de x1,00 ni pasa el tope.</summary>
    public static long MultiplierAt(double elapsedSeconds, double growthPerSecond = DefaultGrowthPerSecond)
    {
        if (elapsedSeconds <= 0)
        {
            return OneX;
        }

        var value = Math.Floor(100 * Math.Exp(growthPerSecond * elapsedSeconds));
        return value >= MaxMultiplier ? MaxMultiplier : Math.Max(OneX, (long)value);
    }

    /// <summary>Cuanto tarda el multiplicador en llegar a <paramref name="multiplier"/> (el momento exacto de una explosion o de un retiro automatico).</summary>
    public static TimeSpan TimeToReach(long multiplier, double growthPerSecond = DefaultGrowthPerSecond)
    {
        if (multiplier <= OneX)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(Math.Log(multiplier / 100.0) / growthPerSecond);
    }

    /// <summary>El pago de una apuesta retirada en <paramref name="multiplier"/>: piso(apuesta x multiplicador), siempre un entero de fichas.</summary>
    public static long PayoutFor(long stake, long multiplier)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stake, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(multiplier, OneX);

        return checked(stake * multiplier / 100);
    }
}
