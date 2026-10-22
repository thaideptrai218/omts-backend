using OMTS.Application.Common.Models;

namespace OMTS.Application.Common.Interfaces.Messaging;

/// <summary>
/// Push notification adapter (formerly API-02). Delivers to the devices the account registered (ACS-07);
/// the real provider is a later ticket, a fake with a dev inbox is used meanwhile.
/// </summary>
public interface IPushSender
{
    Task<DeliveryResult> SendAsync(PushMessage message, CancellationToken cancellationToken = default);
}

public sealed record PushMessage(
    Guid RecipientAccountId,
    string Type,
    string Title,
    string? Body,
    string? RelatedRecordType,
    string? RelatedRecordCode);
