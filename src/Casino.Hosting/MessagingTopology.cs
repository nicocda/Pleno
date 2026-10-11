using Wolverine.RabbitMQ;
using Wolverine.RabbitMQ.Internal;

namespace Casino.Hosting;

/// <summary>
/// La topologia de RabbitMQ del casino: nombres de colas e intercambios (exchanges) y quien se une a quien. Es la unica fuente de verdad
/// y todos los servicios la declaran completa al arrancar (declarar es idempotente): asi el orden en que arranquen no importa y un
/// mensaje nunca se pierde por publicarse antes de que exista la cola del que lo consume.
///
/// Un exchange por TEMA, con una cola por consumidor. Los handlers de Wolverine son globales a cada aplicacion (no importa por que cola
/// llega un mensaje), asi que cada cola recibe solo lo que su servicio maneja: si no, se ejecutarian dos veces.
/// </summary>
public static class MessagingTopology
{
    // Ordenes hacia la Wallet: una cola con un solo dueño.
    public const string WalletCommandsQueue = "wallet.commands";

    public const string WalletStakeEventsExchange = "wallet.stake-events";
    public const string WalletBalanceEventsExchange = "wallet.balance-events";
    public const string GamesEventsExchange = "games.events";
    public const string UsersEventsExchange = "users.events";
    public const string GamesBroadcastExchange = "games.broadcast";

    public const string GamesWalletEventsQueue = "games.wallet-events";
    public const string RealtimeWalletEventsQueue = "realtime.wallet-events";
    public const string RealtimeGameEventsQueue = "realtime.game-events";
    public const string WalletUserEventsQueue = "wallet.user-events";
    public const string RealtimeGameBroadcastQueue = "realtime.game-broadcast";

    /// <summary>Declara colas, intercambios y uniones de todo el sistema. Idempotente.</summary>
    public static void Declare(RabbitMqTransportExpression broker)
    {
        ArgumentNullException.ThrowIfNull(broker);

        broker.DeclareQueue(WalletCommandsQueue);

        // Hechos de la reserva y la liquidacion: los consumen los juegos.
        broker.DeclareExchange(WalletStakeEventsExchange, exchange =>
        {
            exchange.ExchangeType = ExchangeType.Fanout;
            exchange.BindQueue(GamesWalletEventsQueue);
        });

        // Cambios de saldo: los consume el tiempo real para avisar al navegador.
        broker.DeclareExchange(WalletBalanceEventsExchange, exchange =>
        {
            exchange.ExchangeType = ExchangeType.Fanout;
            exchange.BindQueue(RealtimeWalletEventsQueue);
        });

        // Rondas cerradas: las consume el tiempo real.
        broker.DeclareExchange(GamesEventsExchange, exchange =>
        {
            exchange.ExchangeType = ExchangeType.Fanout;
            exchange.BindQueue(RealtimeGameEventsQueue);
        });

        // Hechos en vivo de los juegos para todos los jugadores (por ejemplo, las rondas de Crash): los consume el tiempo real.
        broker.DeclareExchange(GamesBroadcastExchange, exchange =>
        {
            exchange.ExchangeType = ExchangeType.Fanout;
            exchange.BindQueue(RealtimeGameBroadcastQueue);
        });

        // Hechos de usuarios: la Wallet abre la cuenta cuando entra un jugador nuevo.
        broker.DeclareExchange(UsersEventsExchange, exchange =>
        {
            exchange.ExchangeType = ExchangeType.Fanout;
            exchange.BindQueue(WalletUserEventsQueue);
        });
    }
}
