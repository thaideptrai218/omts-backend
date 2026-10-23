using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Models;

namespace OMTS.Infrastructure.Messaging;

/// <summary>Sends email through an SMTP server with MailKit (<c>Email:Provider = Smtp</c>).</summary>
internal sealed class SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task<DeliveryResult> SendAsync(
        string to,
        string subject,
        string htmlBody,
        string textBody,
        IReadOnlyList<EmailAttachment>? attachments = null,
        CancellationToken cancellationToken = default)
    {
        var smtp = options.Value.Smtp;
        if (string.IsNullOrWhiteSpace(smtp.Host) || string.IsNullOrWhiteSpace(smtp.FromAddress))
        {
            logger.LogError("Email to {Recipient} not sent: Email:Smtp:Host or FromAddress is not configured", Masking.Email(to));
            return DeliveryResult.Failed("SMTP_NOT_CONFIGURED");
        }

        try
        {
            var message = BuildMessage(smtp, to, subject, htmlBody, textBody, attachments);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(smtp.TimeoutSeconds));

            using var client = new SmtpClient { Timeout = smtp.TimeoutSeconds * 1000 };
            var security = smtp.Security switch
            {
                SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
                SmtpSecurity.None => SecureSocketOptions.None,
                _ => SecureSocketOptions.StartTls,
            };
            await client.ConnectAsync(smtp.Host, smtp.Port, security, timeout.Token);
            if (!string.IsNullOrEmpty(smtp.Username))
            {
                await client.AuthenticateAsync(smtp.Username, smtp.Password ?? string.Empty, timeout.Token);
            }

            var response = await client.SendAsync(message, timeout.Token);
            await DisconnectQuietlyAsync(client);

            logger.LogInformation("Email {MessageId} sent to {Recipient}", message.MessageId, Masking.Email(to));
            return DeliveryResult.Sent(message.MessageId ?? response);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError("Email to {Recipient} timed out after {Seconds} s", Masking.Email(to), smtp.TimeoutSeconds);
            return DeliveryResult.Failed("SMTP_TIMEOUT");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Provider errors are returned, never thrown, so the caller leaves its data unchanged (MSG04).
            // Never the exception message: a rejected-recipient reply echoes the full address.
            logger.LogError("Email to {Recipient} failed: {Error} {SmtpStatus}", Masking.Email(to), exception.GetType().Name,
                exception is SmtpCommandException command ? $"{command.ErrorCode} {(int)command.StatusCode}" : null);
            return DeliveryResult.Failed("SMTP_ERROR");
        }
    }

    // The mail is accepted once SendAsync returns: a failing QUIT must not report it as failed (the user
    // would retry and get a second code).
    private static async Task DisconnectQuietlyAsync(SmtpClient client)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.DisconnectAsync(true, timeout.Token);
        }
        catch (Exception)
        {
            // Ignored on purpose, see above.
        }
    }

    private static MimeMessage BuildMessage(
        SmtpOptions smtp, string to, string subject, string htmlBody, string textBody, IReadOnlyList<EmailAttachment>? attachments)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(smtp.FromName, smtp.FromAddress));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;

        var body = new BodyBuilder { HtmlBody = htmlBody, TextBody = textBody };
        foreach (var attachment in attachments ?? [])
        {
            body.Attachments.Add(attachment.FileName, attachment.Content, ContentType.Parse(attachment.ContentType));
        }

        message.Body = body.ToMessageBody();
        return message;
    }
}
