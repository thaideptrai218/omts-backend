using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Models;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Messaging;

/// <summary>Messages to a trader by SMS, through the configured SMS sender (<c>Sms:Provider</c>).</summary>
internal sealed class SmsTraderMessageSender(ISmsSender sms) : ITraderMessageSender
{
    public NotificationChannel Channel => NotificationChannel.Sms;

    public Task<DeliveryResult> SendAsync(string phoneNumber, string text, CancellationToken cancellationToken = default) =>
        sms.SendAsync(phoneNumber, text, cancellationToken);
}
