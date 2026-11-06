namespace OMTS.Application.Features.Sales.PurchaseRequests;

/// <summary>SALE-01 step 2: one product, the approximate kg, the pickup date and an optional note.</summary>
public sealed record PlacePurchaseRequest(Guid? ProductId, decimal? RequestedKg, DateOnly? PickupDate, string? Note);

/// <summary>SALE-02 step 3: an optional reason.</summary>
public sealed record CancelPurchaseRequest(string? Reason, string? RowVersion);

/// <summary>SALE-03.0 step 4: the Owner may lower the kg and change the date, and chooses whether the trader may pay later.</summary>
public sealed record AcceptPurchaseRequest(decimal? AcceptedKg, DateOnly? PickupDate, bool? PayLaterAgreed, string? Note, string? RowVersion);

/// <summary>SALE-03.1: the note gives the reason.</summary>
public sealed record DeclinePurchaseRequest(string? Note, string? RowVersion);

/// <summary>The response of the Owner (SALE-10.1): the kg, date and agreement are empty when Declined.</summary>
public sealed record PurchaseRequestResponseView(decimal? AcceptedKg, DateOnly? PickupDate, bool? PayLaterAgreed, string? Note, DateTimeOffset RespondedAt);

/// <summary>The profile of a request, as the Owner decides with it (SCR-SALE-02). The debt is 0 until invoices exist.</summary>
public sealed record PurchaseRequestCustomerView(Guid Id, string Name, string? PhoneNumber, decimal CurrentDebt);

/// <summary>One SMS or Zalo OA sending of the response, kept with its delivery status (BR-SALE-09).</summary>
public sealed record PurchaseRequestSendingView(string Channel, string? PhoneNumber, int? DeliveryStatusId, string? DeliveryStatus, DateTimeOffset? DeliveryStatusAt);

/// <summary>A request as staff see it (SCR-SALE-02).</summary>
/// <param name="Status">"Fulfilled" while <paramref name="Fulfilled"/> is true, with <paramref name="StatusId"/> still Accepted.</param>
public sealed record PurchaseRequestView(
    Guid Id, PurchaseRequestCustomerView Customer, Guid ProductId, string ProductName, decimal RequestedKg, DateOnly PickupDate, string? Note, DateTimeOffset SentAt,
    int StatusId, string Status, bool Fulfilled, PurchaseRequestResponseView? Response, string? CancelReason, DateTimeOffset? CancelledAt,
    IReadOnlyList<PurchaseRequestSendingView> Sendings, string RowVersion);

/// <summary>A request as its trader sees it (SCR-SALE-01): no other customer's data, no price, no internal field.</summary>
public sealed record TraderPurchaseRequestView(
    Guid Id, Guid ProductId, string ProductName, decimal RequestedKg, DateOnly PickupDate, string? Note, DateTimeOffset SentAt,
    int StatusId, string Status, bool Fulfilled, bool CanCancel, PurchaseRequestResponseView? Response, string? CancelReason, DateTimeOffset? CancelledAt, string RowVersion);

/// <summary>A sent request with MSG-GEN-05 (SALE-01 step 5).</summary>
public sealed record PurchaseRequestSaved(TraderPurchaseRequestView Request, string Message, string MessageCode);

/// <summary>
/// My purchases (SALE-10), the requests part; the invoices join it with SALE-10. <paramref name="Message"/> is MSG-GEN-11 when
/// the profile has nothing yet.
/// </summary>
public sealed record MyPurchases(IReadOnlyList<TraderPurchaseRequestView> Requests, int Page, int PageSize, int Total, string? Message, string? MessageCode);
