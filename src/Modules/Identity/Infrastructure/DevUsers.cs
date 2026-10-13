using Casino.BuildingBlocks;
using Casino.Modules.Identity.Application;
using Casino.Modules.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Casino.Modules.Identity.Infrastructure;

/// <summary>
/// Usuarios de desarrollo (los mismos ids, alias y claves que tenia el realm de Keycloak, para no romper scripts ni e2e). SOLO desarrollo: el repositorio
/// es publico y las claves son triviales a proposito. Se activa con <c>Identity:SeedDevUsers=true</c> y unicamente si el entorno es Development.
/// </summary>
internal static class DevUsers
{
    private static readonly (string Alias, string Password, string[] Roles)[] Users =
    [
        ("jugador1", "jugador1-dev", [Roles.Player]),
        ("jugador2", "jugador2-dev", [Roles.Player]),
        ("backoffice1", "backoffice1-dev", [Roles.Player, Roles.Backoffice]),
        ("jefe1", "jefe1-dev", [Roles.Player, Roles.HeadCashier]),
        ("cajero1", "cajero1-dev", [Roles.Player, Roles.Cashier]),
    ];

    /// <summary>Jugadores sinteticos load-001..N para las pruebas de carga (<c>Identity:SeedLoadUsers</c>); datos inventados, con email del dominio reservado .invalid.</summary>
    public static async Task SeedLoadUsersAsync(IServiceProvider services, int count, string password, CancellationToken ct)
    {
        var users = services.GetRequiredService<UserManager<PlayerUser>>();
        var emails = services.GetRequiredService<EmailProtector>();
        var clock = services.GetRequiredService<TimeProvider>();

        for (var i = 1; i <= count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var alias = $"load-{i:D3}";
            if (await users.FindByNameAsync(alias) is not null)
            {
                continue;
            }

            var email = $"{alias}@load.invalid";
            var user = new PlayerUser
            {
                Id = Guid.CreateVersion7(),
                UserName = alias,
                EmailCipher = emails.Encrypt(email, out var version),
                EmailKeyVersion = version,
                EmailHash = emails.Hash(email),
                EmailConfirmed = true,
                CreatedAt = clock.GetUtcNow(),
            };
            if ((await users.CreateAsync(user, password)).Succeeded)
            {
                await users.AddToRoleAsync(user, Roles.Player);
            }
        }
    }

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct)
    {
        var users = services.GetRequiredService<UserManager<PlayerUser>>();
        var emails = services.GetRequiredService<EmailProtector>();
        var clock = services.GetRequiredService<TimeProvider>();

        for (var i = 0; i < Users.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (alias, password, roles) = Users[i];
            if (await users.FindByNameAsync(alias) is not null)
            {
                continue;
            }

            var email = $"{alias}@casino.test";
            var user = new PlayerUser
            {
                Id = Guid.Parse($"0a1b2c3d-000{i + 1}-4000-8000-00000000000{i + 1}"),
                UserName = alias,
                EmailCipher = emails.Encrypt(email, out var version),
                EmailKeyVersion = version,
                EmailHash = emails.Hash(email),
                EmailConfirmed = true,
                CreatedAt = clock.GetUtcNow(),
            };
            // La clave se guarda ya hasheada (sin pasar por las reglas de largo): las de desarrollo son cortas a proposito.
            user.PasswordHash = users.PasswordHasher.HashPassword(user, password);
            var created = await users.CreateAsync(user);
            if (created.Succeeded)
            {
                await users.AddToRolesAsync(user, roles);
            }
        }
    }
}
