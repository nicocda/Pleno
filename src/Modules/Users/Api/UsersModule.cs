using System.Security.Claims;
using Casino.BuildingBlocks;
using Casino.Modules.Users.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Casino.Modules.Users.Api;

public sealed record MeResponse(Guid UserId, string? DisplayName, IReadOnlyList<string> Roles, Guid AccountId, DateTimeOffset RegisteredAt);

public sealed record UserSummary(Guid UserId, DateTimeOffset RegisteredAt);

public static class UsersModule
{
    public static IServiceCollection AddUsersModule(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IOutboxFactory, WolverineOutboxFactory>();
        services.AddSingleton<UserService>();
        return services;
    }

    /// <summary>
    /// Da de alta a todo jugador autenticado la primera vez que llega, sea cual sea el endpoint que toque.
    /// Va despues de la autenticacion. Las peticiones sin token, o de usuarios sin el rol de jugador, no la activan.
    /// </summary>
    public static IApplicationBuilder UsePlayerProvisioning(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.User.Identity?.IsAuthenticated == true
                && context.User.IsInRole(Roles.Player)
                && context.User.TryGetUserId(out var userId))
            {
                await context.RequestServices.GetRequiredService<UserService>().EnsureProvisionedAsync(userId, context.RequestAborted);
            }

            await next();
        });

    public static IEndpointRouteBuilder MapUsersModule(this IEndpointRouteBuilder app)
    {
        // Datos del usuario autenticado. El front lo llama despues del login. El nombre sale del token, no se guarda.
        app.MapGet("/me", async (HttpContext http, UserService users, CancellationToken ct) =>
            {
                var userId = http.User.GetUserId();
                var profile = await users.EnsureProvisionedAsync(userId, ct);
                return Results.Ok(new MeResponse(
                    userId,
                    http.User.FindFirst("preferred_username")?.Value,
                    [.. http.User.GetRoles().Order()],
                    PlayerIds.WalletAccountFor(userId),
                    profile.RegisteredAt));
            })
            .RequireAuthorization(policy => policy.RequireRole(Roles.Player))
            .WithTags("Users");

        // Panel de administracion: quien puede recibir fichas. La cuenta se acredita con /backoffice/wallet/users/{id}/credit.
        app.MapGet("/backoffice/users", async (int? limit, UserService users, CancellationToken ct) =>
            {
                var profiles = await users.ListAsync(Math.Clamp(limit ?? 100, 1, 500), ct);
                return Results.Ok(profiles.Select(p => new UserSummary(p.Id, p.RegisteredAt)));
            })
            .RequireAuthorization(policy => policy.RequireRole(Roles.Backoffice))
            .WithTags("Backoffice");

        return app;
    }
}
