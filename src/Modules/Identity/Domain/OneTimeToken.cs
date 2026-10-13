namespace Casino.Modules.Identity.Domain;

public static class OneTimeTokenPurpose
{
    public const string ConfirmEmail = "confirm_email";
    public const string ResetPassword = "reset_password";

    /// <summary>Codigo de un solo uso que el front cambia por tokens despues de entrar con Google.</summary>
    public const string ExternalLogin = "external_login";

    /// <summary>Marcas (no son tokens) que limitan cuantos correos se mandan por cuenta y tipo.</summary>
    public const string ThrottleVerify = "throttle_verify";

    public const string ThrottleReset = "throttle_reset";
    public const string ThrottleNotice = "throttle_notice";
}

/// <summary>
/// Token de un solo uso (verificar email, reseteo de clave, traspaso del login con Google). Solo se guarda el hash (SHA-256) del valor que viaja
/// por el email o por la URL, asi un volcado de la base no sirve para usarlos.
/// </summary>
public sealed class OneTimeToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string Purpose { get; set; } = string.Empty;

    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? UsedAt { get; set; }
}
