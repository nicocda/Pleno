using System.Net.Mail;
using System.Security.Cryptography;
using Casino.BuildingBlocks;
using Casino.Modules.Identity.Domain;
using Casino.Modules.Identity.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
public sealed partial class AccountService(UserManager<PlayerUser> users, IdentityDb db, EmailProtector emails, AccountFlows flows, TimeProvider clock, ILogger<AccountService> logger)
{
    private const string AliasAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";

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
        // Si el email ya tiene cuenta se responde igual que un alta (no se revela quien esta registrado) y el dueño recibe un aviso.
        if (await db.Users.Where(u => u.EmailHash == hash).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct) is { } existingId)
        {
            await flows.SendAlreadyRegisteredAsync(existingId, email!, ct);
            return new RegisterResult(RegisterOutcome.Created, []);
        }

        var user = NewUser(alias!, email!, hash);
        IdentityResult created;
        try
        {
            created = await users.CreateAsync(user, password ?? string.Empty);
        }
        catch (DbUpdateException)
        {
            // Dos altas simultaneas con el mismo email: gano la otra (indice unico del hash). Se responde igual que un alta.
            return new RegisterResult(RegisterOutcome.Created, []);
        }

        if (!created.Succeeded)
        {
            if (created.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateUserName)))
            {
                return new RegisterResult(RegisterOutcome.AliasTaken, []);
            }

            return new RegisterResult(RegisterOutcome.WeakPassword, [.. created.Errors.Select(e => e.Code)]);
        }

        await users.AddToRoleAsync(user, Roles.Player);
        try
        {
            await flows.SendVerificationAsync(user, email!, ct);
        }
#pragma warning disable CA1031 // La cuenta ya existe: si el correo falla se responde igual (se puede pedir el reenvio) y se deja registro, sin el email.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogVerificationMailFailed(logger, exception);
        }

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

    /// <summary>
    /// Cuenta que corresponde a un login externo (Google). Primero por el vinculo ya guardado; si no, por el email (que el proveedor ya verifico);
    /// si no existe, se crea con un alias generado (no se guarda ni el nombre ni nada mas que el email, cifrado).
    /// </summary>
    public async Task<PlayerUser> FindOrCreateExternalAsync(string provider, string subject, string email, CancellationToken ct)
    {
        if (await users.FindByLoginAsync(provider, subject) is { } linked)
        {
            return linked;
        }

        var hash = emails.Hash(email);
        var existing = await db.Users.FirstOrDefaultAsync(u => u.EmailHash == hash, ct);
        if (existing is not null)
        {
            if (!existing.EmailConfirmed)
            {
                // Defensa contra el alta previa con el email de otro: la cuenta sin verificar pierde la clave y pasa a ser de quien demostro tener el email.
                if (await users.HasPasswordAsync(existing))
                {
                    await users.RemovePasswordAsync(existing);
                }

                existing.EmailConfirmed = true;
                await users.UpdateAsync(existing);
                await users.UpdateSecurityStampAsync(existing);
            }

            await users.AddLoginAsync(existing, new UserLoginInfo(provider, subject, provider));
            return existing;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var user = NewUser(GenerateAlias(), email, hash);
            user.EmailConfirmed = true;
            var created = await users.CreateAsync(user);
            if (created.Succeeded)
            {
                var role = await users.AddToRoleAsync(user, Roles.Player);
                var login = role.Succeeded ? await users.AddLoginAsync(user, new UserLoginInfo(provider, subject, provider)) : role;
                if (!login.Succeeded)
                {
                    // No se deja una cuenta a medias (sin rol o sin vinculo): se borra y el proximo intento empieza de cero.
                    await users.DeleteAsync(user);
                    throw new InvalidOperationException("No se pudo completar la cuenta externa.");
                }

                return user;
            }

            // Solo se reintenta si el alias generado ya existia; cualquier otro fallo se informa.
            if (!created.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateUserName)))
            {
                throw new InvalidOperationException("No se pudo crear la cuenta externa.");
            }
        }

        throw new InvalidOperationException("No se pudo crear la cuenta externa.");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo enviar el correo de verificacion de una cuenta nueva (puede pedir el reenvio)")]
    private static partial void LogVerificationMailFailed(ILogger logger, Exception exception);

    private PlayerUser NewUser(string alias, string email, string hash) => new()
    {
        Id = Guid.CreateVersion7(),
        UserName = alias,
        EmailCipher = emails.Encrypt(email, out var version),
        EmailKeyVersion = version,
        EmailHash = hash,
        CreatedAt = clock.GetUtcNow(),
    };

    private static string GenerateAlias() =>
        "jugador_" + string.Create(6, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = AliasAlphabet[RandomNumberGenerator.GetInt32(AliasAlphabet.Length)];
            }
        });

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
