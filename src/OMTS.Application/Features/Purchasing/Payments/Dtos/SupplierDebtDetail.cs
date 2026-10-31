using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;

namespace OMTS.Application.Features.Purchasing.Payments.Dtos;

public sealed record SupplierDebtDetail(SupplierDebtRow Totals, IReadOnlyList<ReceiptDebtRow> Receipts);
