using Casino.Contracts;
using Casino.Hosting;
using Wolverine;
using Wolverine.RabbitMQ;

/// <summary>Que escucha y que publica el servicio de juegos por RabbitMQ (la topologia completa la declara <see cref="MessagingTopology"/>).</summary>
internal static class GamesMessaging
{
    public static void Configure(WolverineOptions options)
    {
        // Ordenes hacia la Wallet: reservar las fichas de una apuesta y avisar el resultado para que liquide.
        options.PublishMessage<ReserveStake>().ToRabbitQueue(MessagingTopology.WalletCommandsQueue);
        options.PublishMessage<RoundResolved>().ToRabbitQueue(MessagingTopology.WalletCommandsQueue);

        // Hechos de la reserva y la liquidacion que publica la Wallet.
        options.ListenToRabbitQueue(MessagingTopology.GamesWalletEventsQueue).UseDurableInbox();

        // Rondas cerradas: las consume el tiempo real para avisar al jugador.
        options.PublishMessage<RoundClosed>().ToRabbitExchange(MessagingTopology.GamesEventsExchange);

        // Hechos en vivo para todos los jugadores (juegos de ronda compartida): el tiempo real los reenvia a los navegadores.
        options.PublishMessage<GameBroadcast>().ToRabbitExchange(MessagingTopology.GamesBroadcastExchange);
    }
}
