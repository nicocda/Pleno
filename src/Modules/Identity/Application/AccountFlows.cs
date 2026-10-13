using Casino.Modules.Identity.Domain;
using Casino.Modules.Identity.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;

namespace Casino.Modules.Identity.Application;

public enum ResetOutcome
{
    Done,
    InvalidToken,
    WeakPassword,
}

/// <summary>Verificacion de email y recuperacion de clave. Los correos van a la direccion que escribio quien lo pide, nunca a una guardada que haya que descifrar.</summary>
public sealed class AccountFlows(
    UserManager<PlayerUser> users,
    IdentityDb db,
    EmailProtector emails,
    OneTimeTokenService tokens,
    IEmailSender sender,
    IOpenIddictTokenManager openIddictTokens,
    IOptions<IdentityLinksOptions> links)
{
    private static readonly TimeSpan ConfirmLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan ResetLifetime = TimeSpan.FromHours(1);

    public async Task SendVerificationAsync(PlayerUser user, string email, CancellationToken ct)
    {
        var token = await tokens.IssueAsync(user.Id, OneTimeTokenPurpose.ConfirmEmail, ConfirmLifetime, ct);
        await sender.SendAsync(
            new EmailMessage(
                email.Trim(),
                "Confirma tu email",
                $"Hola {user.UserName}, confirma tu email para poder entrar: {Link("verificar", token)}\n\nSi no creaste la cuenta, ignora este mensaje."),
            ct);
    }

    /// <summary>Aviso cuando alguien se registra con un email que ya tiene cuenta: el dueño se entera y puede recuperar la clave, y quien intenta no aprende nada.</summary>
    public Task SendAlreadyRegisteredAsync(string email, CancellationToken ct) =>
        sender.SendAsync(
            new EmailMessage(
                email.Trim(),
                "Ya tienes una cuenta",
                $"Alguien intento registrarse con este email, pero ya tiene una cuenta. Si fuiste tu y no recuerdas la clave, puedes cambiarla aqui: {Link("recuperar", null)}"),
            ct);

    public async Task<bool> VerifyEmailAsync(string? token, CancellationToken ct)
    {
        if (await tokens.ConsumeAsync(token, OneTimeTokenPurpose.ConfirmEmail, ct) is not { } userId)
        {
            return false;
        }

        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return false;
        }

        user.EmailConfirmed = true;
        return (await users.UpdateAsync(user)).Succeeded;
    }

    /// <summary>Reenvia la verificacion solo si el email tiene una cuenta sin verificar. Quien llama responde siempre lo mismo.</summary>
    public async Task ResendVerificationAsync(string? email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return;
        }

        var hash = emails.Hash(email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.EmailHash == hash && !u.EmailConfirmed, ct);
        if (user is not null)
        {
            await SendVerificationAsync(user, email, ct);
        }
    }

    /// <summary>Si el email tiene cuenta, manda el enlace para elegir una clave nueva. Quien llama responde siempre lo mismo.</summary>
    public async Task ForgotPasswordAsync(string? email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return;
        }

        var hash = emails.Hash(email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.EmailHash == hash, ct);
        if (user is null)
        {
            return;
        }

        var token = await tokens.IssueAsync(user.Id, OneTimeTokenPurpose.ResetPassword, ResetLifetime, ct);
        await sender.SendAsync(
            new EmailMessage(email.Trim(), "Recupera tu clave", $"Elige una clave nueva en este enlace (vale 1 hora): {Link("recuperar", token)}\n\nSi no lo pediste, ignora este mensaje."),
            ct);
    }

    public async Task<ResetOutcome> ResetPasswordAsync(string? token, string? newPassword, CancellationToken ct)
    {
        // Primero se comprueba la clave, para no gastar el token si el usuario escribio una clave debil y tiene que reintentar.
        var probe = new PlayerUser();
        foreach (var validator in users.PasswordValidators)
        {
            if (!(await validator.ValidateAsync(users, probe, newPassword)).Succeeded)
            {
                return ResetOutcome.WeakPassword;
            }
        }

        if (await tokens.ConsumeAsync(token, OneTimeTokenPurpose.ResetPassword, ct) is not { } userId
            || await users.FindByIdAsync(userId.ToString()) is not { } user)
        {
            return ResetOutcome.InvalidToken;
        }

        var resetToken = await users.GeneratePasswordResetTokenAsync(user);
        var reset = await users.ResetPasswordAsync(user, resetToken, newPassword!);
        if (!reset.Succeeded)
        {
            return ResetOutcome.WeakPassword;
        }

        // Quien recibio el enlace controla el email: queda verificado, se levanta el bloqueo y se cierran las sesiones abiertas.
        user.EmailConfirmed = true;
        await users.UpdateAsync(user);
        await users.ResetAccessFailedCountAsync(user);
        await users.SetLockoutEndDateAsync(user, null);
        await users.UpdateSecurityStampAsync(user);
        await openIddictTokens.RevokeBySubjectAsync(user.Id.ToString(), ct);
        return ResetOutcome.Done;
    }

    private string Link(string path, string? token)
    {
        var baseUrl = links.Value.PublicWebUrl.TrimEnd('/');
        return token is null ? $"{baseUrl}/{path}" : $"{baseUrl}/{path}?token={Uri.EscapeDataString(token)}";
    }
}
