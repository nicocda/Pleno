using Casino.BuildingBlocks;
using Casino.Modules.Wallet.Application;
using Marten;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Casino.Modules.Wallet.Api;

public static class WalletModule
{
    /// <summary>Requiere que el host registre antes Marten, Wolverine y el outbox (compartidos entre modulos).</summary>
    public static IServiceCollection AddWalletModule(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IOutboxFactory, WolverineOutboxFactory>();
        services.AddSingleton(sp =>
        {
            // Tiempo que una reserva puede quedar abierta antes de liberarse sola. Debe ser mucho mayor que lo que tarda un juego en resolver.
            var configured = sp.GetRequiredService<IConfiguration>()["Wallet:ReservationTtlSeconds"];
            var ttl = int.TryParse(configured, out var seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : WalletService.DefaultReservationTtl;

            return new WalletService(
                sp.GetRequiredService<IDocumentStore>(),
                sp.GetRequiredService<TimeProvider>(),
                outbox: sp.GetRequiredService<IOutboxFactory>(),
                reservationTtl: ttl);
        });
        services.AddSingleton<BackofficeAudit>();
        services.AddSingleton(sp => CashierOptions.Load(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<CashierService>();
        services.AddSingleton<WithdrawalService>();
        return services;
    }

    /// <summary>
    /// Mapea los endpoints de la Wallet. SIN autenticacion todavia (llega con Keycloak en la fase 3):
    /// por eso el host solo los expone en Development.
    /// </summary>
    public static IEndpointRouteBuilder MapWalletModule(this IEndpointRouteBuilder app)
    {
        WalletEndpoints.Map(app);
        return app;
    }
}
