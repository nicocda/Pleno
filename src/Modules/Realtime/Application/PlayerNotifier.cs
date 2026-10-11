using Casino.Contracts;
using Casino.Modules.Realtime.Api;
using Microsoft.AspNetCore.SignalR;

namespace Casino.Modules.Realtime.Application;

/// <summary>Lo que recibe el navegador cuando cambia el saldo. <c>Version</c> permite descartar avisos fuera de orden.</summary>
public sealed record BalanceChangedNotice(long Available, long Reserved, long Version);

/// <summary>Lo que recibe el navegador cuando una ronda termina.</summary>
public sealed record RoundClosedNotice(
    Guid BetId,
    string Game,
    string Status,
    int? WinningNumber,
    long Stake,
    long Payout,
    string? FailureReason);

/// <summary>Un hecho en vivo de un juego de ronda compartida (por ejemplo, "exploto el cohete"). <c>Data</c> es el JSON propio del juego.</summary>
public sealed record GameEventNotice(string Game, string Kind, System.Text.Json.JsonElement Data, DateTimeOffset At);

/// <summary>Empuja avisos al grupo de la cuenta. Un jugador solo esta en el grupo de SU cuenta, asi que solo recibe lo suyo.</summary>
public sealed class PlayerNotifier(IHubContext<PlayerHub> hub)
{
    public const string BalanceChangedMethod = "balanceChanged";
    public const string RoundClosedMethod = "roundClosed";
    public const string GameEventMethod = "gameEvent";

    public Task BalanceChangedAsync(BalanceChanged message, CancellationToken ct) =>
        hub.Clients.Group(PlayerHub.GroupFor(message.AccountId))
            .SendAsync(BalanceChangedMethod, new BalanceChangedNotice(message.Available, message.Reserved, message.Version), ct);

    /// <summary>Difunde a TODOS los jugadores conectados (no a una cuenta): es informacion publica de la ronda, sin datos de nadie.</summary>
    public Task GameBroadcastAsync(GameBroadcast message, CancellationToken ct)
    {
        using var document = System.Text.Json.JsonDocument.Parse(message.Data);
        return hub.Clients.Group(PlayerHub.EveryoneGroup)
            .SendAsync(GameEventMethod, new GameEventNotice(message.Game, message.Kind, document.RootElement.Clone(), message.At), ct);
    }

    public Task RoundClosedAsync(RoundClosed message, CancellationToken ct) =>
        hub.Clients.Group(PlayerHub.GroupFor(message.AccountId))
            .SendAsync(
                RoundClosedMethod,
                new RoundClosedNotice(
                    message.BetId, message.Game, message.Status, message.WinningNumber, message.Stake, message.Payout, message.FailureReason),
                ct);
}
