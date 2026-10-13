using System.Net;
using System.Net.Mail;
using Casino.Modules.Identity.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casino.Modules.Identity.Infrastructure;

/// <summary>Envio por SMTP (Brevo, SES, Resend...). Las credenciales vienen de secretos.</summary>
internal sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var smtp = options.Value.Smtp;
        using var client = new SmtpClient(smtp.Host, smtp.Port) { EnableSsl = smtp.EnableSsl };
        if (!string.IsNullOrEmpty(smtp.User))
        {
            client.Credentials = new NetworkCredential(smtp.User, smtp.Password);
        }

        using var mail = new MailMessage(options.Value.From, message.To, message.Subject, message.Body);
        await client.SendMailAsync(mail, ct);
    }
}

/// <summary>Solo desarrollo: escribe el correo en el log (asunto y cuerpo, nunca el destinatario) para poder seguir los enlaces a mano.</summary>
internal sealed partial class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        LogEmail(logger, message.Subject, message.Body);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[DEV] Correo '{Subject}': {Body}")]
    private static partial void LogEmail(ILogger logger, string subject, string body);
}
