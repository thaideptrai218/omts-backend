namespace OMTS.Application.Common.Models.Payments;

/// <summary>
/// One item of a Checkout SDK order. Its JSON is part of the signed text, so the property order (<c>id</c>, then <c>amount</c>) must be the one the Mini App sends to
/// Zalo: the client passes the item on as it received it.
/// </summary>
public sealed record ZaloCheckoutItem(string Id, long Amount);
