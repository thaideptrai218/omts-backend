namespace OMTS.Application.Features.Sales.Weighings.Dtos;

/// <summary>A weighing as both sides read it.</summary>
/// <param name="CustomerId">The customer profile of the request, which the invoice made from the weighing names.</param>
/// <param name="ConfirmedAt">When the trader confirmed it; null while it can still be corrected.</param>
/// <param name="InvoiceId">The Completed invoice made from it; null until then.</param>
/// <param name="RowVersion">Sent back to correct or confirm it.</param>
public sealed record PortWeighingView(
    Guid Id, Guid PurchaseRequestId, string PurchaseRequestCode, Guid CustomerId, string CustomerName, Guid ProductId, string ProductName, int AcceptedCages, DateTimeOffset WeighedAt,
    IReadOnlyList<PortWeighingLotView> Lots, int CageCount, decimal TotalKg, DateTimeOffset? ConfirmedAt, Guid? InvoiceId, string? InvoiceCode, string RowVersion);
