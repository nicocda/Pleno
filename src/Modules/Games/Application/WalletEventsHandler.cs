using Casino.Contracts;
using Casino.Modules.Games.Platform;

namespace Casino.Modules.Games.Application;

// Handler de Wolverine: consume la cola games.wallet-events (los hechos de la Wallet, via exchange fanout).
// Cada metodo es idempotente: la entrega es "al menos una vez".

// El mensaje solo trae el BetId: se reparte a todos los juegos habilitados y cada uno busca esa apuesta en lo suyo (si no es suya, la ignora).
public sealed class WalletEventsHandler(IEnumerable<IGameRounds> games)
{
    private readonly IGameRounds[] _games = [.. games];

    public async Task Handle(StakeReserved message, CancellationToken ct)
    {
        foreach (var game in _games)
        {
            await game.OnStakeReservedAsync(message, ct);
        }
    }

    public async Task Handle(StakeRejected message, CancellationToken ct)
    {
        foreach (var game in _games)
        {
            await game.OnStakeRejectedAsync(message, ct);
        }
    }

    public async Task Handle(StakeSettled message, CancellationToken ct)
    {
        foreach (var game in _games)
        {
            await game.OnStakeSettledAsync(message, ct);
        }
    }

    public async Task Handle(StakeReleased message, CancellationToken ct)
    {
        foreach (var game in _games)
        {
            await game.OnStakeReleasedAsync(message, ct);
        }
    }

    public async Task Handle(StakeSettlementRejected message, CancellationToken ct)
    {
        foreach (var game in _games)
        {
            await game.OnSettlementRejectedAsync(message, ct);
        }
    }
}
