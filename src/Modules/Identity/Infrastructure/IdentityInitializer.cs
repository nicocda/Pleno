using Casino.BuildingBlocks;
using Casino.Modules.Identity.Api;
using Casino.Modules.Identity.Domain;
using Casino.Modules.Identity.Application;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;

namespace Casino.Modules.Identity.Infrastructure;

/// <summary>Al arrancar: crea la base y las tablas si faltan, los roles del casino y el cliente OAuth publico del front.</summary>
internal sealed class IdentityInitializer(IServiceProvider services, IOptions<IdentityLinksOptions> links) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDb>();
        await db.Database.EnsureCreatedAsync(cancellationToken);

        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var role in IdentityRoles.All)
        {
            if (!await roles.RoleExistsAsync(role))
            {
                await roles.CreateAsync(new IdentityRole<Guid>(role));
            }
        }

        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        if (await applications.FindByClientIdAsync(IdentityModule.WebClientId, cancellationToken) is null)
        {
            // Cliente publico (sin secreto): el front. Solo puede usar los grants que el servidor ya habilita.
            await applications.CreateAsync(
                new OpenIddictApplicationDescriptor
                {
                    ClientId = IdentityModule.WebClientId,
                    ClientType = OpenIddictConstants.ClientTypes.Public,
                    Permissions =
                    {
                        OpenIddictConstants.Permissions.Endpoints.Token,
                        OpenIddictConstants.Permissions.GrantTypes.Password,
                        OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
                        OpenIddictConstants.Permissions.Prefixes.GrantType + TokenEndpoint.ExternalCodeGrant,
                        OpenIddictConstants.Permissions.Scopes.Email,
                        OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OfflineAccess,
                    },
                },
                cancellationToken);
        }

        // Los alias de IdentityLinksOptions.AdminAliases reciben el rol de backoffice (el primer administrador sale de la configuracion).
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PlayerUser>>();
        foreach (var alias in links.Value.AdminAliases)
        {
            if (await users.FindByNameAsync(alias) is { } admin && !await users.IsInRoleAsync(admin, Roles.Backoffice))
            {
                await users.AddToRoleAsync(admin, Roles.Backoffice);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
