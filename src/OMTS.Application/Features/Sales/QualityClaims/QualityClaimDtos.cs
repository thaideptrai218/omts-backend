using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.QualityClaims;

/// <summary>CLAIM-01 step 2: the description; the photos come as files.</summary>
/// <param name="OtherFilePart">The request held a file part with another name than the photos.</param>
public sealed record SubmitQualityClaim(string? Description, bool OtherFilePart = false);

/// <summary>CLAIM-02.0: the offer, and a note if needed, for example the price cut that is offered.</summary>
public sealed record AcceptQualityClaim(QualityClaimOffer? Offer, string? Note, string? RowVersion);

/// <summary>CLAIM-02.1: the note gives the reason.</summary>
public sealed record RejectQualityClaim(string? Note, string? RowVersion);

public sealed record QualityClaimCustomerView(Guid Id, string Name, string? PhoneNumber);

public sealed record QualityClaimInvoiceRef(Guid Id, string Code);

/// <summary>A row of the list: the claim, its invoice, and for staff the profile. The photos are in the detail.</summary>
public sealed record QualityClaimRow(
    Guid Id, string Code, DateTimeOffset SentAt, int StatusId, string Status, string Description, int PhotoCount, QualityClaimOffer? Offer,
    QualityClaimInvoiceRef Invoice, QualityClaimCustomerView? Customer);

/// <summary>A page of claims with MSG-GEN-11 or MSG-GEN-10 when it is empty.</summary>
public sealed record QualityClaimsPage(IReadOnlyList<QualityClaimRow> Items, int Page, int PageSize, int Total, string? Message, string? MessageCode);

/// <summary>A link that works for an hour, as on the provenance page; the photos are private (BR-CLAIM-01).</summary>
public sealed record QualityClaimPhotoView(int SortOrder, string Url);

public sealed record QualityClaimResponseView(string? Note, DateTimeOffset RespondedAt);

public sealed record QualityClaimInvoiceLineView(string ProductName, string LotCode, decimal Kg, decimal? UnitPrice, decimal? Amount);

public sealed record QualityClaimPriceCutView(DateOnly CutDate, decimal Amount, string Reason);

/// <summary>What CLAIM-02 step 1 shows of the invoice: its lines, harvest lots, kg, amounts, status and any price cut. Data-entry corrections join it with SALE-07.</summary>
public sealed record QualityClaimInvoiceView(
    Guid Id, string Code, DateOnly InvoiceDate, Guid ZoneId, string ZoneName, int StatusId, string Status, decimal Subtotal, decimal Discount, decimal OtherCharges,
    decimal PriceCutTotal, decimal Total, IReadOnlyList<QualityClaimInvoiceLineView> Lines, IReadOnlyList<QualityClaimPriceCutView> PriceCuts);

/// <summary>One SMS or Zalo OA sending of the response, with its delivery status (BR-CLAIM-02, GFR-06).</summary>
public sealed record QualityClaimSendingView(string Channel, string? PhoneNumber, int? DeliveryStatusId, string? DeliveryStatus, DateTimeOffset? DeliveryStatusAt);

/// <summary>A claim as staff read it (SCR-CLAIM-02). <paramref name="RowVersion"/> goes back with the response.</summary>
public sealed record QualityClaimView(
    Guid Id, string Code, DateTimeOffset SentAt, int StatusId, string Status, string Description, IReadOnlyList<QualityClaimPhotoView> Photos, QualityClaimOffer? Offer,
    QualityClaimResponseView? Response, QualityClaimCustomerView Customer, QualityClaimInvoiceView Invoice, IReadOnlyList<QualityClaimSendingView> Sendings, string RowVersion);

/// <summary>A claim as its trader reads it: no profile or zone data, no prices, no sendings, no row version.</summary>
public sealed record TraderQualityClaimView(
    Guid Id, string Code, DateTimeOffset SentAt, int StatusId, string Status, string Description, IReadOnlyList<QualityClaimPhotoView> Photos, QualityClaimOffer? Offer,
    QualityClaimResponseView? Response, QualityClaimInvoiceRef Invoice, DateOnly InvoiceDate, decimal InvoiceTotal, IReadOnlyList<QualityClaimInvoiceLineView> Lines);

/// <summary>A sent claim with MSG-GEN-05 (CLAIM-01 step 5).</summary>
public sealed record QualityClaimSaved(TraderQualityClaimView Claim, string Message, string MessageCode);
