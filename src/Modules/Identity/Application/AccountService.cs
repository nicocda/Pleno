using System.Net.Mail;
using Casino.BuildingBlocks;
using Casino.Modules.Identity.Domain;
using Casino.Modules.Identity.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Casino.Modules.Identity.Application;

public enum RegisterOutcome
{
    Created,
    InvalidAlias,
    InvalidEmail,
    AliasTaken,
    WeakPassword,
}

public sealed record RegisterResult(RegisterOutcome Outcome, IReadOnlyList<string> PasswordErrors);

/// <summary>Altas y busquedas de cuentas. El email entra por aca y se protege antes de tocar la base.</summary>
public sealed class AccountService(UserManager<PlayerUser> users, IdentityDb db, EmailProtector emails, TimeProvider clock)
{
    public async Task<RegisterResult> RegisterAsync(string? alias, string? email, string? password, CancellationToken ct)
    {
        if (!AliasRules.IsValid(alias))
        {
            return new RegisterResult(RegisterOutcome.InvalidAlias, []);
        }

        if (!IsValidEmail(email))
        {
            return new RegisterResult(RegisterOutcome.InvalidEmail, []);
        }

        var hash = emails.Hash(email!);
        // Si el email ya tiene cuenta se responde igual que un alta (no se revela quien esta registrado); no se crea nada.
        if (await db.Users.AnyAsync(u => u.EmailHash == hash, ct))
        {
            return new RegisterResult(RegisterOutcome.Created, []);
        }

        var user = new PlayerUser
        {
            Id = Guid.CreateVersion7(),
            UserName = alias,
            EmailCipher = emails.Encrypt(email!, out var version),
            EmailKeyVersion = version,
            EmailHash = hash,
            CreatedAt = clock.GetUtcNow(),
        };

        var created = await users.CreateAsync(user, password ?? string.Empty);
        if (!created.Succeeded)
        {
            if (created.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateUserName)))
            {
                return new RegisterResult(RegisterOutcome.AliasTaken, []);
            }

            return new RegisterResult(RegisterOutcome.WeakPassword, [.. created.Errors.Select(e => e.Code)]);
        }

        await users.AddToRoleAsync(user, Roles.Player);
        return new RegisterResult(RegisterOutcome.Created, []);
    }

    /// <summary>Entrar con el alias o con el email (si trae una arroba se busca por el hash).</summary>
    public async Task<PlayerUser?> FindForLoginAsync(string? login, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(login))
        {
            return null;
        }

        if (login.Contains('@', StringComparison.Ordinal))
        {
            var hash = emails.Hash(login);
            return await db.Users.FirstOrDefaultAsync(u => u.EmailHash == hash, ct);
        }

        return await users.FindByNameAsync(login.Trim());
    }

    private static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254)
        {
            return false;
        }

        var trimmed = email.Trim();
        return MailAddress.TryCreate(trimmed, out var parsed) && parsed.Address == trimmed && parsed.Host.Contains('.', StringComparison.Ordinal);
    }
}
