using Casino.Modules.Identity.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Casino.Modules.Identity.Infrastructure;

/// <summary>Cada hora borra los tokens de un solo uso (y las marcas de limite de correos) vencidos hace mas de un dia.</summary>
internal sealed partial class TokenCleanup(IServiceScopeFactory scopes, ILogger<TokenCleanup> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var purged = await scope.ServiceProvider.GetRequiredService<OneTimeTokenService>().PurgeAsync(stoppingToken);
                if (purged > 0)
                {
                    LogPurged(logger, purged);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
#pragma warning disable CA1031 // Una falla de limpieza no debe tumbar el servicio: se reintenta en la proxima hora.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogFailed(logger, exception);
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Se borraron {Count} tokens de un solo uso vencidos")]
    private static partial void LogPurged(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo limpiar los tokens de un solo uso vencidos")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
