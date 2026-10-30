using Microsoft.Extensions.Logging;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Models.Messaging;
using OMTS.Application.Common.Models;
using OMTS.Application.Common;

namespace OMTS.Infrastructure.Messaging;

/// <summary><c>Push:Provider = None</c>: push is skipped (the in-app notification row is still saved).</summary>
internal sealed class UnconfiguredPushSender(ILogger<UnconfiguredPushSender> logger) : IPushSender
{
    public Task<DeliveryResult> SendAsync(PushMessage message, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Push {Type} to account {AccountId} not sent: no push provider is configured", message.Type, message.RecipientAccountId);
        return Task.FromResult(DeliveryResult.Failed(UnconfiguredSenders.NotConfigured));
    }
}
