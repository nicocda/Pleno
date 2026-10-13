using Casino.BuildingBlocks;
using Casino.Modules.Identity.Application;
using Casino.Modules.Identity.Domain;
using Casino.Modules.Identity.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Casino.Modules.Identity.Api;

public sealed record AdminUser(Guid Id, string Alias, IReadOnlyList<string> Roles, bool EmailConfirmed, DateTimeOffset CreatedAt);

public sealed record SetRolesRequest(string[]? Roles);

/// <summary>Administracion de cuentas para el backoffice: ver jugadores (solo alias) y darles o quitarles roles. Nunca expone ni el email ni la clave.</summary>
internal static class AdminEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/identity/admin")
            .RequireAuthorization(policy => policy.RequireRole(Roles.Backoffice))
            .WithTags("Identity admin");

        group.MapGet("/users", async (string? q, int? limit, IdentityDb db, CancellationToken ct) =>
        {
            var query = db.Users.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToUpperInvariant();
                query = query.Where(u => u.NormalizedUserName!.StartsWith(term));
            }

            var page = await query.OrderByDescending(u => u.CreatedAt).Take(Math.Clamp(limit ?? 50, 1, 200)).ToListAsync(ct);
            var ids = page.Select(u => u.Id).ToList();
            var roleRows = await (from userRole in db.UserRoles
                                  join role in db.Roles on userRole.RoleId equals role.Id
                                  where ids.Contains(userRole.UserId)
                                  select new { userRole.UserId, role.Name }).ToListAsync(ct);
            return Results.Ok(page.Select(u => new AdminUser(
                u.Id,
                u.UserName!,
                [.. roleRows.Where(r => r.UserId == u.Id).Select(r => r.Name!).Order()],
                u.EmailConfirmed,
                u.CreatedAt)));
        });

        // Reemplaza los roles de la cuenta. Todos son jugadores, asi que `player` se conserva siempre.
        group.MapPut("/users/{id:guid}/roles", async (Guid id, SetRolesRequest request, HttpContext http, UserManager<PlayerUser> manager, IOpenIddictTokenManager openIddictTokens, CancellationToken ct) =>
        {
            var wanted = (request.Roles ?? []).Append(Roles.Player).Distinct().ToList();
            if (wanted.Any(role => !IdentityRoles.All.Contains(role)))
            {
                return Results.BadRequest(new { error = "unknown_role" });
            }

            var user = await manager.FindByIdAsync(id.ToString());
            if (user is null)
            {
                return Results.NotFound();
            }

            // Un administrador no se quita a si mismo el rol: evita dejar el sistema sin nadie que pueda administrar.
            if (http.User.FindFirst(Claims.Subject)?.Value == id.ToString() && !wanted.Contains(Roles.Backoffice))
            {
                return Results.BadRequest(new { error = "cannot_remove_own_backoffice" });
            }

            var current = await manager.GetRolesAsync(user);
            // Siempre tiene que quedar al menos un administrador.
            if (current.Contains(Roles.Backoffice) && !wanted.Contains(Roles.Backoffice) && (await manager.GetUsersInRoleAsync(Roles.Backoffice)).Count <= 1)
            {
                return Results.BadRequest(new { error = "last_backoffice" });
            }

            var removed = await manager.RemoveFromRolesAsync(user, current.Except(wanted));
            var added = removed.Succeeded ? await manager.AddToRolesAsync(user, wanted.Except(current)) : removed;
            if (!added.Succeeded)
            {
                return Results.Problem("No se pudieron guardar los roles.");
            }

            // Los tokens ya emitidos llevan los roles viejos: se cierran las sesiones para que el proximo login los refleje.
            await manager.UpdateSecurityStampAsync(user);
            await openIddictTokens.RevokeBySubjectAsync(id.ToString(), ct);
            return Results.NoContent();
        });
    }
}
