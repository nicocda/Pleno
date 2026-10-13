using Casino.Modules.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Casino.Modules.Identity.Infrastructure;

/// <summary>Base <c>identitydb</c>: cuentas, roles y los tokens/clientes de OpenIddict. Solo el servicio de identidad la toca.</summary>
public sealed class IdentityDb(DbContextOptions<IdentityDb> options) : IdentityDbContext<PlayerUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<OneTimeToken> OneTimeTokens => Set<OneTimeToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        base.OnModelCreating(builder);

        builder.Entity<PlayerUser>(user =>
        {
            // El email nunca va en claro: se anulan las columnas estandar y se busca por el hash con clave.
            user.Ignore(u => u.Email).Ignore(u => u.NormalizedEmail);
            user.Property(u => u.EmailCipher).HasMaxLength(512);
            user.Property(u => u.EmailHash).HasMaxLength(64);
            user.HasIndex(u => u.EmailHash).IsUnique();
        });

        builder.Entity<OneTimeToken>(token =>
        {
            token.HasKey(t => t.Id);
            token.Property(t => t.Purpose).HasMaxLength(32);
            token.Property(t => t.TokenHash).HasMaxLength(64);
            token.HasIndex(t => t.TokenHash).IsUnique();
        });
    }
}
