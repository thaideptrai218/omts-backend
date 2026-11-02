using OMTS.Application.Features.Employees;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts.Dtos;

/// <param name="CancelPayments">Required for a Completed receipt with Recorded payments: cancel them, or keep them as an unallocated supplier amount.</param>
public sealed record CancelReceiptRequest(string? Reason, bool? CancelPayments, string? RowVersion);
