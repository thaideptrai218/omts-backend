using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Suppliers.Dtos;

public sealed record SupplierView(
    Guid Id,
    string Name,
    SupplierType SupplierType,
    string PhoneNumber,
    string? Address,
    string? Email,
    IReadOnlyList<SupplierCertificate> Certificates,
    int StatusId,
    string Status,
    IReadOnlyList<SupplierReceiptRow> Receipts,
    string RowVersion);
