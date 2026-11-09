namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>A sale awaiting its transfer names a record that no longer exists, so its invoice cannot be saved when the transfer arrives (GFR-08 then keeps the money).</summary>
/// <param name="reason">Why, as the audit log of the stopped sale states it, e.g. "harvest lot L-001 no longer exists."</param>
public sealed class PendingSaleNotSavableException(string reason) : Exception(reason);
