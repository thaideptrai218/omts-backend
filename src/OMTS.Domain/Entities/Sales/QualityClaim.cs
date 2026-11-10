using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Sales;

/// <summary>
/// A quality problem a trader reports in the app on one sales invoice (CLAIM-01, BR-CLAIM-01). It is on the whole invoice and records no kg. The Cooperative Owner responds once
/// (CLAIM-02, BR-CLAIM-02); the response changes neither the invoice nor the customer debt, and the return or price cut that may follow is not linked to it.
/// </summary>
public class QualityClaim : AuditableEntity, IHasStatus
{
    public const int MaxDescriptionLength = 2000;
    public const int MaxPhotos = 10;

    public string Code { get; set; } = string.Empty;

    public Guid InvoiceId { get; set; }
    public SalesInvoice Invoice { get; set; } = null!;

    /// <summary>The profile of the invoice when the claim was sent: who may read it (BR-CLAIM-01). A merge re-points it (SALE-04.3).</summary>
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public DateTimeOffset SentAt { get; set; }

    /// <summary>What the trader reports, up to <see cref="MaxDescriptionLength"/> characters.</summary>
    public string Description { get; set; } = string.Empty;

    public ICollection<QualityClaimPhoto> Photos { get; set; } = [];

    /// <summary>Set when Accepted (BR-CLAIM-02); null otherwise.</summary>
    public QualityClaimOffer? Offer { get; set; }

    /// <summary>Optional when Accepted; the reason when Rejected.</summary>
    public string? ResponseNote { get; set; }

    public DateTimeOffset? RespondedAt { get; set; }
    public Guid? RespondedByAccountId { get; set; }

    public int StatusId { get; set; } = StatusIds.QualityClaim.AwaitingResponse;
    public Status Status { get; set; } = null!;
}

/// <summary>One photo of a claim (at least one, at most <see cref="QualityClaim.MaxPhotos"/>), seen only by the trader who sent it and the staff (BR-CLAIM-01).</summary>
public class QualityClaimPhoto : Entity
{
    public Guid ClaimId { get; set; }
    public QualityClaim Claim { get; set; } = null!;

    /// <summary>1, 2, … in the order sent.</summary>
    public int SortOrder { get; set; }

    public string FileKey { get; set; } = string.Empty;
}
