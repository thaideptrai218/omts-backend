using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;

namespace OMTS.Application.Features.Purchasing.Payments.Dtos;

public sealed record ReceiptDebtRow(
    Guid ReceiptId, string Code, DateOnly ReceiptDate, decimal Payable, decimal Returned, decimal Paid, decimal Refunded, decimal Outstanding);
