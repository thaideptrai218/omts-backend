using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Suppliers;

public sealed record SaveSupplierRequest(string? Name, SupplierType? SupplierType, string? PhoneNumber, string? OtherContacts, string? RowVersion = null);

public sealed record SupplierListItem(Guid Id, string Name, SupplierType SupplierType, string PhoneNumber, int StatusId, string Status);

public sealed record SupplierCertificate(int Index, string Url);

public sealed record SupplierReceiptRow(Guid Id, string Code, DateOnly? ReceiptDate, Guid ZoneId, string Status, decimal Payable, decimal Outstanding);

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
