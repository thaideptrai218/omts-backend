using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Suppliers.Dtos;

/// <param name="Debt">Current supplier debt (INP-06): outstanding of Completed receipts − unallocated payments.</param>
public sealed record SupplierView(
    Guid Id,
    string Name,
    SupplierType SupplierType,
    string PhoneNumber,
    string? OtherContacts,
    IReadOnlyList<SupplierCertificate> Certificates,
    int StatusId,
    string Status,
    decimal Debt,
    IReadOnlyList<SupplierReceiptRow> Receipts,
    string RowVersion);
