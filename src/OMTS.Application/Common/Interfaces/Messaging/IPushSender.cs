using OMTS.Application.Common.Models.Messaging;
using OMTS.Application.Common.Models;

namespace OMTS.Application.Common.Interfaces.Messaging;

/// <summary>
/// Push notification adapter (formerly API-02). Delivers to <see cref="PushMessage.DeviceTokens"/> only;
/// the real provider is a later ticket, a fake with a dev inbox is used meanwhile.
/// </summary>
public interface IPushSender
{
    Task<DeliveryResult> SendAsync(PushMessage message, CancellationToken cancellationToken = default);
}
