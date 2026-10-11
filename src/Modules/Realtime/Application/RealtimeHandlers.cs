using Casino.Contracts;

namespace Casino.Modules.Realtime.Application;

// Handlers de Wolverine para las colas realtime.wallet-events (solo BalanceChanged) y realtime.game-events (solo RoundClosed).
// Cada cola recibe unicamente lo que este modulo usa: los handlers de Wolverine son globales a la aplicacion, asi que si una
// cola recibiera mensajes que tambien maneja otro modulo, se ejecutarian dos veces.
// La entrega es "al menos una vez": un aviso repetido es inofensivo (el navegador usa la version para descartarlo).

public sealed class BalanceChangedHandler(PlayerNotifier notifier)
{
    public Task Handle(BalanceChanged message, CancellationToken ct) => notifier.BalanceChangedAsync(message, ct);
}

public sealed class GameBroadcastHandler(PlayerNotifier notifier)
{
    public Task Handle(GameBroadcast message, CancellationToken ct) => notifier.GameBroadcastAsync(message, ct);
}

public sealed class RoundClosedHandler(PlayerNotifier notifier)
{
    public Task Handle(RoundClosed message, CancellationToken ct) => notifier.RoundClosedAsync(message, ct);
}
