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

/// <summary>Los ultimos correos que "salieron" en desarrollo (sin SMTP). Solo en memoria: nunca van al log ni a la telemetria, porque traen enlaces con tokens.</summary>
public sealed class DevMailbox
{
    private const int Capacity = 50;
    private readonly Lock _lock = new();
    private readonly List<EmailMessage> _messages = [];

    public void Add(EmailMessage message)
    {
        lock (_lock)
        {
            _messages.Add(message);
            if (_messages.Count > Capacity)
            {
                _messages.RemoveAt(0);
            }
        }
    }

    public IReadOnlyList<EmailMessage> Snapshot()
    {
        lock (_lock)
        {
            return [.. _messages];
        }
    }
}

/// <summary>Solo desarrollo: guarda el correo en <see cref="DevMailbox"/> (se lee en <c>GET /identity/dev/emails</c>) y en el log deja unicamente el asunto.</summary>
internal sealed partial class DevEmailSender(DevMailbox mailbox, ILogger<DevEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        mailbox.Add(message);
        LogEmail(logger, message.Subject);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[DEV] Correo '{Subject}' guardado en GET /identity/dev/emails")]
    private static partial void LogEmail(ILogger logger, string subject);
}
