using OMTS.Application.Common.Models;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Messaging;

/// <summary>
/// The channel of the messages to a trader's phone: SMS, or Zalo OA once its templates are approved (§4.1 actors,
/// BR-SALE-03). Only the SMS route exists, over <see cref="ISmsSender"/> (Fake or None until a provider is chosen); Zalo OA
/// comes as one more implementation when the templates are approved, and <c>TraderMessageService</c> does not change.
/// </summary>
public interface ITraderMessageSender
{
    /// <summary>The channel the sending is kept with.</summary>
    NotificationChannel Channel { get; }

    Task<DeliveryResult> SendAsync(string phoneNumber, string text, CancellationToken cancellationToken = default);
}
