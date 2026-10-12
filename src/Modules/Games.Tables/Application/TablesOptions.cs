using Microsoft.Extensions.Configuration;

namespace Casino.Modules.Games.Tables;

/// <summary>
/// Configuracion de los juegos de mesas entre jugadores (seccion <c>Tables</c>). Los valores por defecto sirven; se validan al arrancar.
/// </summary>
public sealed class TablesOptions
{
    /// <summary>Cuanto "piensa" un bot antes de jugar (para que se pueda seguir la partida).</summary>
    public int BotThinkMilliseconds { get; set; } = 1_500;

    /// <summary>Cuanto puede esperar una mesa abierta sin iniciarse antes de cancelarse (se devuelven las fichas).</summary>
    public double OpenTableMinutes { get; set; } = 15;

    /// <summary>Cuanto dura la reserva de las fichas en la Wallet: tiene que alcanzar para esperar jugadores y jugar la partida entera.</summary>
    public int ReservationSeconds { get; set; } = 3_600;

    /// <summary>
    /// Cada cuanto el motor renueva las reservas de una partida en curso, para que una partida larga no pierda las fichas reservadas (la Wallet las libera sola a los <see cref="ReservationSeconds"/>).
    /// Tiene que ser bastante menor que ese plazo. Si el motor se cae, deja de renovar y la reserva vence sola.
    /// </summary>
    public int ReservationRenewSeconds { get; set; } = 600;

    /// <summary>
    /// Cuanto puede estar inactiva una mesa continua (nadie hace nada) antes de cerrarse si esta esperando jugadores o todos estan ausentes: se liquidan los asientos y se libera la mesa.
    /// </summary>
    public double CashIdleMinutes { get; set; } = 15;

    /// <summary>Cada cuanto mira el motor si hay bots que jugar o turnos vencidos.</summary>
    public int TickMilliseconds { get; set; } = 300;

    /// <summary>Si el motor de mesas corre en este servicio. Hay que dejarlo prendido en UNA sola instancia del servicio de juegos.</summary>
    public bool EngineEnabled { get; set; } = true;

    public static TablesOptions Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection("Tables").Get<TablesOptions>() ?? new TablesOptions();
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (BotThinkMilliseconds is < 0 or > 30_000 || TickMilliseconds is < 20 or > 5_000)
        {
            throw new InvalidOperationException("Tables: BotThinkMilliseconds va de 0 a 30.000 y TickMilliseconds de 20 a 5.000.");
        }

        if (OpenTableMinutes is < 0.05 or > 1_440 || ReservationSeconds is < 60 or > 3_600)
        {
            throw new InvalidOperationException("Tables: OpenTableMinutes va de 0,05 a 1.440 y ReservationSeconds de 60 a 3.600.");
        }

        if (CashIdleMinutes is < 0.05 or > 1_440)
        {
            throw new InvalidOperationException("Tables: CashIdleMinutes va de 0,05 a 1.440.");
        }

        if (ReservationRenewSeconds < 1 || ReservationRenewSeconds >= ReservationSeconds)
        {
            throw new InvalidOperationException("Tables: ReservationRenewSeconds tiene que ser de al menos 1 y menor que ReservationSeconds (se renueva antes de que venza).");
        }
    }
}
