using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;

namespace OMTS.Application.Features.Purchasing.Payments.Dtos;

public sealed record SupplierDebtRow(
    Guid SupplierId, string SupplierName, decimal Payable, decimal Returned, decimal Paid, decimal Refunded, decimal Unallocated, decimal Outstanding);
