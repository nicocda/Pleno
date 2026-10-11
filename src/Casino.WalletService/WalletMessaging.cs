using Casino.Contracts;
using Casino.Hosting;
using Wolverine;
using Wolverine.RabbitMQ;

/// <summary>Que escucha y que publica la Wallet por RabbitMQ (la topologia completa la declara <see cref="MessagingTopology"/>).</summary>
internal static class WalletMessaging
{
    public static void Configure(WolverineOptions options)
    {
        // Escucha: las ordenes de los juegos y los hechos de usuarios (un jugador nuevo = abrir su cuenta).
        options.ListenToRabbitQueue(MessagingTopology.WalletCommandsQueue).UseDurableInbox();
        options.ListenToRabbitQueue(MessagingTopology.WalletUserEventsQueue).UseDurableInbox();

        // Se programa a si misma el vencimiento de cada reserva: si sigue abierta al vencer, se libera.
        options.PublishMessage<ExpireReservation>().ToRabbitQueue(MessagingTopology.WalletCommandsQueue);

        // Hechos de la reserva y la liquidacion: los consumen los juegos.
        options.PublishMessage<StakeReserved>().ToRabbitExchange(MessagingTopology.WalletStakeEventsExchange);
        options.PublishMessage<StakeRejected>().ToRabbitExchange(MessagingTopology.WalletStakeEventsExchange);
        options.PublishMessage<StakeSettled>().ToRabbitExchange(MessagingTopology.WalletStakeEventsExchange);
        options.PublishMessage<StakeReleased>().ToRabbitExchange(MessagingTopology.WalletStakeEventsExchange);
        options.PublishMessage<StakeSettlementRejected>().ToRabbitExchange(MessagingTopology.WalletStakeEventsExchange);

        // Cambios de saldo: los consume el tiempo real para avisar al navegador.
        options.PublishMessage<BalanceChanged>().ToRabbitExchange(MessagingTopology.WalletBalanceEventsExchange);
    }
}
