
namespace OMTS.Application.Common.Models.Persistence;

/// <summary>Σ of the Completed receipts of one supplier, aggregated in the database.</summary>
public sealed record SupplierDebtFigures(Guid SupplierId, string SupplierName, decimal Payable, decimal Returned, decimal Paid, decimal Refunded)
{
    public decimal Outstanding => Payable - Returned - (Paid - Refunded);
}
