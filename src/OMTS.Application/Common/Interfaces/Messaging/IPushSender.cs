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

/// <param name="DeviceTokens">
/// The recipient's devices that receive push now (ACS-07: registered by a session that is still logged in,
/// notifications allowed); empty when there is none.
/// </param>
/// <param name="NotificationId">The kept notification the push announces, so the app opens it (ACCOUNT-08.2).</param>
/// <param name="MessageCode">SRS code of <paramref name="Body"/>.</param>
/// <param name="MessageParams">
/// The parameters of <paramref name="MessageCode"/> as a JSON object (<c>MessageParameterJson</c>), so the device shows the
/// text in its own language; null when the message has none.
/// </param>
public sealed record PushMessage(
    Guid RecipientAccountId,
    string Type,
    string Title,
    string? Body,
    string? RelatedRecordType,
    string? RelatedRecordCode,
    IReadOnlyList<string> DeviceTokens,
    Guid? NotificationId = null,
    string? MessageCode = null,
    Guid? RelatedRecordId = null,
    string? MessageParams = null);
