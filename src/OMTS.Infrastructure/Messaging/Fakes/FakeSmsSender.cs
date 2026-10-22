using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Models;

namespace OMTS.Infrastructure.Messaging.Fakes;

/// <summary>
/// SMS sender used until a real provider is chosen: nothing leaves the server; messages are kept in
/// <see cref="Inbox"/> for the dev-only inbox endpoints and integration tests. Logs carry only the masked
/// number and the message id, never the text (verification codes).
/// </summary>
public sealed class FakeSmsSender(IOptions<SmsOptions> options, TimeProvider timeProvider, ILogger<FakeSmsSender> logger) : ISmsSender
{
    public FakeMessageInbox Inbox { get; } = new();

    public Task<DeliveryResult> SendAsync(string phoneNumber, string text, CancellationToken cancellationToken = default)
    {
        if (options.Value.Fake.FailFor.Contains(phoneNumber, StringComparer.OrdinalIgnoreCase))
        {
            logger.LogWarning("Fake SMS to {Recipient} failed (configured to fail)", Masking.Phone(phoneNumber));
            return Task.FromResult(DeliveryResult.Failed("FAKE_CONFIGURED_FAILURE"));
        }

        var id = Guid.NewGuid().ToString("N");
        Inbox.Add(new FakeMessage(id, timeProvider.GetUtcNow(), phoneNumber, null, text));
        logger.LogInformation("Fake SMS {MessageId} to {Recipient} kept in the dev inbox", id, Masking.Phone(phoneNumber));
        return Task.FromResult(DeliveryResult.Sent(id));
    }
}
