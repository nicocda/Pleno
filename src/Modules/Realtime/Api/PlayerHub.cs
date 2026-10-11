using Casino.BuildingBlocks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Casino.Modules.Realtime.Api;

/// <summary>
/// Canal en vivo de un jugador. Exige token con rol player. El servidor decide a que grupo entra cada conexion
/// (el de SU cuenta, derivada del token) y el hub no expone ningun metodo invocable por el cliente: el navegador solo escucha.
/// </summary>
[Authorize(Roles = Roles.Player)]
public sealed class PlayerHub : Hub
{
    public const string Path = "/hubs/player";

    /// <summary>Grupo al que entran todos los jugadores conectados: recibe lo que los juegos en vivo difunden a todos.</summary>
    public const string EveryoneGroup = "players:everyone";

    public static string GroupFor(Guid accountId) => $"account:{accountId:N}";

    public override async Task OnConnectedAsync()
    {
        var accountId = PlayerIds.WalletAccountFor(Context.User!.GetUserId());
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(accountId));
        await Groups.AddToGroupAsync(Context.ConnectionId, EveryoneGroup);
        await base.OnConnectedAsync();
    }
}
