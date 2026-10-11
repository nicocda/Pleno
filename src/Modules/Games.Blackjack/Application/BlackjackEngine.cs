using Casino.Modules.Games.Blackjack;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Casino.Modules.Games.Application;

/// <summary>
/// El motor de mesas de Blackjack: un proceso en segundo plano con UN ciclo por mesa (las mesas corren en paralelo; dentro de una mesa todo es secuencial, asi
/// no hay carreras entre acciones). Cada mano: se abre y espera la primera apuesta (sin reloj ni trabajo inutil), corre la ventana de apuestas, reparte, da
/// el turno a cada jugador (si no actua a tiempo se planta), juega el crupier, paga y espera una pausa. Debe correr en UNA sola instancia del servicio de
/// juegos (<c>Blackjack:EngineEnabled</c>).
/// </summary>
public sealed partial class BlackjackEngine(BlackjackService blackjack, TimeProvider clock, ILogger<BlackjackEngine> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private const int RearmEveryPolls = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await blackjack.RecoverAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            LogRecoverFailed(logger, ex);
        }

        await Task.WhenAll(blackjack.Options.Tables.Select(table => RunTableAsync(table.Id, stoppingToken)));
    }

    private async Task RunTableAsync(string tableId, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOneRoundAsync(tableId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Una mano que falla no puede frenar la mesa: se registra, se corta lo que quedo a medias y se sigue con la siguiente.
                LogRoundFailed(logger, tableId, ex);
                await SafeDelayAsync(TimeSpan.FromSeconds(1), stoppingToken);
                await SafeRecoverAsync(stoppingToken);
            }
        }
    }

    /// <summary>Una mano completa de una mesa, de punta a punta. Publica para que las pruebas puedan correr una sola.</summary>
    public async Task RunOneRoundAsync(string tableId, CancellationToken ct)
    {
        var options = blackjack.Options;
        var round = await blackjack.OpenRoundAsync(tableId, ct);

        // 1. Esperar a que alguien apueste y cerrar la ventana. Si al cerrar nadie llego a reservar, la mano vuelve a esperar.
        IReadOnlyList<BlackjackBet>? seated;
        while (true)
        {
            round = await WaitForBettingEndAsync(round.Id, ct);
            seated = await blackjack.DealAsync(round.Id, ct);
            if (seated is not null)
            {
                break;
            }
        }

        // 2. Turnos: cada jugador decide hasta que se planta, se pasa o se le acaba el tiempo.
        round = await blackjack.GetRoundAsync(round.Id, ct);
        if (!round.DealerRevealed)
        {
            foreach (var seat in seated)
            {
                await PlayTurnAsync(round.Id, seat.Id, ct);
            }

            // 3. Juega el crupier (solo hace falta si quedo alguien con una mano para vencer).
            var hands = new List<BlackjackBet>();
            foreach (var seat in seated)
            {
                hands.Add(await blackjack.GetBetAsync(seat.Id, ct));
            }

            round = await blackjack.RevealHoleAsync(round.Id, ct);
            if (hands.Any(b => b.Hand == HandState.Stood))
            {
                while (BlackjackMath.DealerMustDraw(round.DealerCards))
                {
                    await SafeDelayAsync(TimeSpan.FromMilliseconds(options.DealerStepMilliseconds), ct);
                    round = await blackjack.DealerDrawAsync(round.Id, ct);
                }
            }
        }

        // 4. Pagar, revelar la semilla y esperar la pausa.
        await blackjack.SettleAsync(round.Id, ct);
        await SafeDelayAsync(TimeSpan.FromSeconds(options.PauseSeconds), ct);
    }

    private async Task<BlackjackRound> WaitForBettingEndAsync(Guid roundId, CancellationToken ct)
    {
        for (var polls = 0; ; polls++)
        {
            var round = await blackjack.GetRoundAsync(roundId, ct);
            if (round.BettingEndsAt is { } endsAt)
            {
                await DelayUntilAsync(endsAt, ct);
                return round;
            }

            // Una vez por segundo: si hay una apuesta reservada pero el reloj no arranco (la reserva llego tarde), se arranca.
            if (polls % RearmEveryPolls == RearmEveryPolls - 1)
            {
                await blackjack.RearmClockAsync(roundId, ct);
            }

            await Task.Delay(PollInterval, clock, ct);
        }
    }

    private async Task PlayTurnAsync(Guid roundId, Guid betId, CancellationToken ct)
    {
        var bet = await blackjack.GetBetAsync(betId, ct);
        if (bet.Hand != HandState.Playing)
        {
            return; // blackjack natural: no juega
        }

        var endsAt = await blackjack.StartTurnAsync(roundId, betId, ct);
        while (clock.GetUtcNow() < endsAt)
        {
            bet = await blackjack.GetBetAsync(betId, ct);
            if (bet.Hand != HandState.Playing)
            {
                return;
            }

            await Task.Delay(PollInterval, clock, ct);
        }

        await blackjack.AutoStandAsync(betId, ct);
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
            await blackjack.RecoverAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRecoverFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "No se pudieron recuperar las manos de Blackjack que quedaron a medias.")]
    private static partial void LogRecoverFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Una mano de Blackjack fallo en la mesa {TableId}; se corta y se sigue con la siguiente.")]
    private static partial void LogRoundFailed(ILogger logger, string tableId, Exception exception);
}
