using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Models;

namespace OMTS.Infrastructure.Messaging.Fakes;

/// <summary>
/// Email sender for Development and tests (<c>Email:Provider = Fake</c>): keeps messages in
/// <see cref="Inbox"/> instead of sending them, so tests can read verification codes.
/// </summary>
public sealed class FakeEmailSender(IOptions<EmailOptions> options, TimeProvider timeProvider, ILogger<FakeEmailSender> logger) : IEmailSender
{
    public FakeMessageInbox Inbox { get; } = new();

    public Task<DeliveryResult> SendAsync(
        string to,
        string subject,
        string htmlBody,
        string textBody,
        IReadOnlyList<EmailAttachment>? attachments = null,
        CancellationToken cancellationToken = default)
    {
        if (options.Value.Fake.FailFor.Contains(to, StringComparer.OrdinalIgnoreCase))
        {
            logger.LogWarning("Fake email to {Recipient} failed (configured to fail)", Masking.Email(to));
            return Task.FromResult(DeliveryResult.Failed("FAKE_CONFIGURED_FAILURE"));
        }

        var id = Guid.NewGuid().ToString("N");
        Inbox.Add(new FakeMessage(id, timeProvider.GetUtcNow(), to, subject, textBody));
        logger.LogInformation("Fake email {MessageId} to {Recipient} kept in the dev inbox", id, Masking.Email(to));
        return Task.FromResult(DeliveryResult.Sent(id));
    }
}
