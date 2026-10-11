using Casino.Contracts;
using Casino.Hosting;
using Casino.Modules.Realtime.Application;
using Casino.Modules.Users.Infrastructure;
using Wolverine;
using Wolverine.RabbitMQ;

/// <summary>
/// Composicion del host principal (puerta de entrada): su propia base de datos (usersdb) con el modulo de usuarios y la mensajeria de
/// ESTOS modulos (usuarios y tiempo real). La Wallet y los juegos son servicios aparte: aca solo se escuchan sus hechos por RabbitMQ.
/// </summary>
internal static class CasinoInfrastructure
{
    public const string SchemaName = "casino";

    public static WebApplicationBuilder AddCasinoInfrastructure(this WebApplicationBuilder builder)
    {
        builder.AddCasinoData("usersdb", SchemaName, UsersMartenConfiguration.Register);
        builder.AddCasinoMessaging("api", [typeof(PlayerNotifier).Assembly], Configure);
        return builder;
    }

    private static void Configure(WolverineOptions options)
    {
        // Cambios de saldo (los publica la Wallet) y rondas cerradas (las publican los juegos): el tiempo real avisa al navegador.
        options.ListenToRabbitQueue(MessagingTopology.RealtimeWalletEventsQueue).UseDurableInbox();
        options.ListenToRabbitQueue(MessagingTopology.RealtimeGameEventsQueue).UseDurableInbox();

        // Hechos en vivo de los juegos de ronda compartida: se reenvian a todos los navegadores conectados.
        options.ListenToRabbitQueue(MessagingTopology.RealtimeGameBroadcastQueue).UseDurableInbox();

        // Hechos de usuarios: la Wallet abre la cuenta cuando entra un jugador nuevo.
        options.PublishMessage<UserRegistered>().ToRabbitExchange(MessagingTopology.UsersEventsExchange);
    }
}
