using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Sales;

/// <summary>
/// A quality problem a trader reports in the app on one sales invoice (CLAIM-01, BR-CLAIM-01). It is on the whole invoice and records no kg. The Cooperative Owner responds once
/// (CLAIM-02, BR-CLAIM-02): a rejection, or an offer of a return or a price cut that the trader accepts or declines (PM answer 12/11). Neither changes the invoice or the customer debt; the
/// return or price cut that follows an accepted offer is not linked to the claim.
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

    /// <summary>What the Owner proposed (BR-CLAIM-02); set from Offer made on, null when the Owner rejected the claim.</summary>
    public QualityClaimOffer? Offer { get; set; }

    /// <summary>The amount of a price cut offer, its own field (PM answer 12/11): required when <see cref="Offer"/> is a price cut, above 0 and at most the invoice total; null for a return.</summary>
    public decimal? OfferAmount { get; set; }

    /// <summary>Optional with an offer; the reason when the Owner rejected the claim.</summary>
    public string? ResponseNote { get; set; }

    public DateTimeOffset? RespondedAt { get; set; }
    public Guid? RespondedByAccountId { get; set; }

    /// <summary>When the trader accepted or declined the offer (null before).</summary>
    public DateTimeOffset? AnsweredAt { get; set; }

    /// <summary>The optional note of the trader with their answer.</summary>
    public string? AnswerNote { get; set; }

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
