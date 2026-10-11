using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Casino.Modules.Games.Tables;

/// <summary>
/// El motor de las mesas entre jugadores: un proceso en segundo plano que, cada pocos cientos de milisegundos, juega por los bots, juega por quien dejo vencer su
/// turno (y pasa a un bot a quien se ausenta varias veces seguidas) y cancela las mesas abiertas que nadie inicio. Las partidas viven en la base, asi que sobreviven
/// a un reinicio. Debe correr en UNA sola instancia del servicio de juegos (<c>Tables:EngineEnabled</c>).
/// </summary>
public sealed partial class TableEngine(TableService tables, TimeProvider clock, ILogger<TableEngine> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tick = TimeSpan.FromMilliseconds(tables.Options.TickMilliseconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await tables.TickAsync(stoppingToken);
                await Task.Delay(tick, clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Un tick que falla no puede frenar a las demas mesas: se registra y se sigue.
                LogTickFailed(logger, ex);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Fallo un ciclo del motor de mesas; se sigue con el siguiente.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);
}
