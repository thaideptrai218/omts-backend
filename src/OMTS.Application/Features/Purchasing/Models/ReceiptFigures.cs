using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Models;

/// <summary>Money figures of one purchase receipt, all in whole VND (INP-03…INP-07).</summary>
/// <param name="Payable">Subtotal − discount.</param>
/// <param name="Returned">Σ return values of its purchase returns.</param>
/// <param name="Paid">Σ Recorded payments allocated to the receipt.</param>
/// <param name="Refunded">Σ refunds received on its returns.</param>
/// <param name="Outstanding">Net payable (payable − returned) − net paid (paid − refunded).</param>
public sealed record ReceiptFigures(decimal Subtotal, decimal Discount, decimal Payable, decimal Returned, decimal Paid, decimal Refunded, decimal Outstanding)
{
    public decimal NetPayable => Payable - Returned;

    public decimal NetPaid => Paid - Refunded;
}
