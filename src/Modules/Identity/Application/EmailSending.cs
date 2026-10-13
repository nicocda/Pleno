namespace Casino.Modules.Identity.Application;

/// <summary>Un correo saliente. El destinatario es PII: nunca se escribe en logs ni trazas.</summary>
public sealed record EmailMessage(string To, string Subject, string Body);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

/// <summary>
/// Datos de envio y de los enlaces. <c>Email:Smtp:*</c> (host, puerto, usuario, clave) son secretos fuera de Development.
/// Sin <c>Host</c>, solo en Development, los correos se guardan en memoria (<c>DevMailbox</c>) y se leen en un endpoint de desarrollo; nunca se escriben en el log.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public string From { get; set; } = "no-reply@casino.local";

    public SmtpOptions Smtp { get; set; } = new();
}

public sealed class SmtpOptions
{
    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    public string? User { get; set; }

    public string? Password { get; set; }

    public bool EnableSsl { get; set; } = true;
}

/// <summary>Donde vive el front (para armar los enlaces de los correos) y a que origenes se puede volver despues de entrar con Google.</summary>
public sealed class IdentityLinksOptions
{
    public const string SectionName = "Identity";

    public string PublicWebUrl { get; set; } = "http://localhost:5173";

    public string[] AllowedReturnOrigins { get; set; } = ["http://localhost:5173"];

    /// <summary>Alias que reciben el rol de backoffice al arrancar (si existen). Es la forma de nombrar al primer administrador.</summary>
    public string[] AdminAliases { get; set; } = [];
}
