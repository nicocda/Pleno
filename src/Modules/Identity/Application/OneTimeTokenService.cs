using System.Security.Cryptography;
using System.Text;
using Casino.Modules.Identity.Domain;
using Casino.Modules.Identity.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Casino.Modules.Identity.Application;

public sealed class OneTimeTokenService(IdentityDb db, TimeProvider clock)
{
    /// <summary>Crea un token y devuelve el valor en claro (es lo unico que se envia; en la base queda su hash).</summary>
    public async Task<string> IssueAsync(Guid userId, string purpose, TimeSpan lifetime, CancellationToken ct)
    {
        var raw = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        db.Set<OneTimeToken>().Add(new OneTimeToken
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Purpose = purpose,
            TokenHash = Hash(raw),
            ExpiresAt = clock.GetUtcNow().Add(lifetime),
        });
        await db.SaveChangesAsync(ct);
        return raw;
    }

    /// <summary>Gasta el token (una sola vez, de forma atomica) y devuelve el usuario; null si no existe, ya se uso, vencio o es de otro proposito.</summary>
    public async Task<Guid?> ConsumeAsync(string? raw, string purpose, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 128)
        {
            return null;
        }

        var hash = Hash(raw);
        var now = clock.GetUtcNow();
        var spent = await db.Set<OneTimeToken>()
            .Where(t => t.TokenHash == hash && t.Purpose == purpose && t.UsedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.UsedAt, now), ct);
        if (spent != 1)
        {
            return null;
        }

        return await db.Set<OneTimeToken>().Where(t => t.TokenHash == hash).Select(t => (Guid?)t.UserId).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Limite de envios por cuenta y tipo: deja una marca que vence en <paramref name="window"/> y avisa si ya habia una vigente. Evita usar el servicio para llenar de
    /// correos la casilla de otra persona. Las marcas se barren junto con los tokens vencidos.
    /// </summary>
    public async Task<bool> TryClaimSlotAsync(Guid userId, string kind, TimeSpan window, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (await db.Set<OneTimeToken>().AnyAsync(t => t.UserId == userId && t.Purpose == kind && t.ExpiresAt > now, ct))
        {
            return false;
        }

        db.Set<OneTimeToken>().Add(new OneTimeToken
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Purpose = kind,
            TokenHash = Hash(Guid.NewGuid().ToString("N")),
            ExpiresAt = now.Add(window),
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Borra los tokens y marcas vencidos hace mas de un dia (los usados tambien), para que la tabla no crezca sin limite.</summary>
    public async Task<int> PurgeAsync(CancellationToken ct)
    {
        var limit = clock.GetUtcNow().AddDays(-1);
        return await db.Set<OneTimeToken>().Where(t => t.ExpiresAt < limit).ExecuteDeleteAsync(ct);
    }

    private static string Hash(string raw) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
