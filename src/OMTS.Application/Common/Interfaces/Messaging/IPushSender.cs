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
public sealed record PushMessage(
    Guid RecipientAccountId,
    string Type,
    string Title,
    string? Body,
    string? RelatedRecordType,
    string? RelatedRecordCode,
    IReadOnlyList<string> DeviceTokens);
