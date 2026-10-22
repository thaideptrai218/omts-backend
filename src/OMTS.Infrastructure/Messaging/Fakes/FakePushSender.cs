using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Models;

namespace OMTS.Infrastructure.Messaging.Fakes;

/// <summary>Push sender used until a real provider is chosen; logs recipient and type, keeps a dev inbox.</summary>
public sealed class FakePushSender(IOptions<PushOptions> options, TimeProvider timeProvider, ILogger<FakePushSender> logger) : IPushSender
{
    public FakeMessageInbox Inbox { get; } = new();

    public Task<DeliveryResult> SendAsync(PushMessage message, CancellationToken cancellationToken = default)
    {
        var recipient = message.RecipientAccountId.ToString();
        if (options.Value.Fake.FailFor.Contains(recipient, StringComparer.OrdinalIgnoreCase))
        {
            logger.LogWarning("Fake push {Type} to account {Recipient} failed (configured to fail)", message.Type, recipient);
            return Task.FromResult(DeliveryResult.Failed("FAKE_CONFIGURED_FAILURE"));
        }

        var id = Guid.NewGuid().ToString("N");
        Inbox.Add(new FakeMessage(id, timeProvider.GetUtcNow(), recipient, message.Title, $"{message.Type}: {message.Body}"));
        logger.LogInformation("Fake push {MessageId} {Type} to account {Recipient}", id, message.Type, recipient);
        return Task.FromResult(DeliveryResult.Sent(id));
    }
}
