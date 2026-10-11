using Casino.BuildingBlocks;
using Casino.Modules.Games.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Casino.Modules.Games.Api;

/// <summary>El catalogo: los juegos que este servicio tiene habilitados. El lobby se arma con esto, no con una lista fija.</summary>
internal static class GamesCatalogEndpoints
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/games", (IEnumerable<IGameModule> games) => Results.Ok(games.Select(g => g.Info)))
            .RequireAuthorization(policy => policy.RequireRole(Roles.Player))
            .WithTags("Games");
}
