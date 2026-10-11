using Casino.Modules.Games.Crash;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Casino.Modules.Games.Application;

/// <summary>
/// El motor de rondas de Crash: un proceso en segundo plano que abre una ronda, espera la ventana de apuestas, deja subir el multiplicador, hace los
/// retiros automaticos en su momento exacto, explota la ronda cuando corresponde, espera una pausa y vuelve a empezar. Todos los tiempos salen de
/// la hora de inicio de la ronda y de la formula del multiplicador (no de contar "ticks"), asi un retraso no corre el resultado.
/// Debe correr en UNA sola instancia del servicio de juegos (<c>Crash:EngineEnabled</c>).
/// </summary>
public sealed partial class CrashEngine(CrashService crash, TimeProvider clock, ILogger<CrashEngine> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await crash.RecoverAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            LogRecoverFailed(logger, ex);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOneRoundAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Una ronda que falla no puede frenar el juego: se registra, se corta lo que quedo a medias y se sigue con la siguiente.
                LogRoundFailed(logger, ex);
                await SafeDelayAsync(TimeSpan.FromSeconds(1), stoppingToken);
                await SafeRecoverAsync(stoppingToken);
            }
        }
    }

    /// <summary>Una ronda completa, de punta a punta. Publica para que las pruebas puedan correr una sola.</summary>
    public async Task RunOneRoundAsync(CancellationToken ct)
    {
        var options = crash.Options;
        var round = await crash.OpenRoundAsync(ct);
        await DelayUntilAsync(round.BettingEndsAt, ct);

        round = await crash.StartRoundAsync(round.Id, ct);
        var startedAt = round.StartedAt!.Value;
        var crashPoint = crash.CrashPointOf(round);

        // Los retiros automaticos ocurren en el instante en que el multiplicador llega al que pidio cada jugador.
        foreach (var bet in await crash.AutoCashOutsAsync(round.Id, crashPoint, ct))
        {
            await DelayUntilAsync(startedAt + CrashMath.TimeToReach(bet.AutoCashOut!.Value, round.GrowthPerSecond), ct);
            await crash.AutoCashOutAsync(bet.Id, ct);
        }

        await DelayUntilAsync(startedAt + CrashMath.TimeToReach(crashPoint, round.GrowthPerSecond), ct);
        await crash.CrashRoundAsync(round.Id, ct);

        await SafeDelayAsync(TimeSpan.FromSeconds(options.PauseSeconds), ct);
    }

    private async Task DelayUntilAsync(DateTimeOffset moment, CancellationToken ct)
    {
        var wait = moment - clock.GetUtcNow();
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, clock, ct);
        }
    }

    private async Task SafeDelayAsync(TimeSpan wait, CancellationToken ct)
    {
        if (wait <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            await Task.Delay(wait, clock, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // el servicio se esta apagando
        }
    }

    private async Task SafeRecoverAsync(CancellationToken ct)
    {
        try
        {
            await crash.RecoverAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRecoverFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "No se pudo recuperar las rondas de Crash que quedaron a medias.")]
    private static partial void LogRecoverFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Una ronda de Crash fallo; se corta y se sigue con la siguiente.")]
    private static partial void LogRoundFailed(ILogger logger, Exception exception);
}
