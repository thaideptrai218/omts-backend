using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>One SMS or Zalo OA sending of the response to a claim (BR-CLAIM-02, GFR-06).</summary>
public sealed record QualityClaimSending(NotificationChannel Channel, string? PhoneNumber, int? DeliveryStatusId, DateTimeOffset? DeliveryStatusAt);

public interface IQualityClaimRepository
{
    void Add(QualityClaim claim);

    /// <summary>Whether the invoice has a claim Awaiting response (BR-CLAIM-01).</summary>
    Task<bool> HasAwaitingAsync(Guid invoiceId, CancellationToken cancellationToken);

    /// <summary>The claim with its customer profile and invoice, tracked, to respond to it.</summary>
    Task<QualityClaim?> GetForEditAsync(Guid claimId, CancellationToken cancellationToken);

    /// <summary>The claim with what its detail shows (photos, profile, invoice lines), read-only.</summary>
    Task<QualityClaim?> GetDetailAsync(Guid claimId, CancellationToken cancellationToken);

    /// <summary>One page of the claims of invoices in these zones: Awaiting response first, then newest first (SCR-CLAIM-02).</summary>
    Task<(IReadOnlyList<QualityClaim> Items, int Total)> SearchAsync(PermittedZones zones, int? statusId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>One page of the claims of a profile, newest first (the trader's list).</summary>
    Task<(IReadOnlyList<QualityClaim> Items, int Total)> ListOfCustomerAsync(Guid customerId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>The sendings kept for this claim, oldest first.</summary>
    Task<IReadOnlyList<QualityClaimSending>> ListSendingsAsync(Guid claimId, CancellationToken cancellationToken);
}
