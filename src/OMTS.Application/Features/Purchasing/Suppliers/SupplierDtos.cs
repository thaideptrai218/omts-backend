using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Suppliers;

/// <param name="Address">Optional, at most 500 characters.</param>
/// <param name="Email">Optional; a valid address when entered (stored lower-case).</param>
public sealed record SaveSupplierRequest(
    string? Name, SupplierType? SupplierType, string? PhoneNumber, string? Address = null, string? Email = null, string? RowVersion = null);

public sealed record SupplierListItem(Guid Id, string Name, SupplierType SupplierType, string PhoneNumber, int StatusId, string Status);

public sealed record SupplierCertificate(int Index, string Url);

public sealed record SupplierReceiptRow(Guid Id, string Code, DateOnly? ReceiptDate, Guid ZoneId, string Status, decimal Payable, decimal Outstanding);

/// <summary>
/// A supplier with its certificates and its latest receipts. The supplier debt is not here: it is PURCHASE-04, which the
/// Zone Manager does not hold (§I.4.4); it is read from the supplier debt endpoints.
/// </summary>
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
