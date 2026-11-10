using OMTS.Application.Common;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Suppliers;

/// <param name="Address">Optional, at most 500 characters.</param>
/// <param name="Email">Optional; a valid address when entered (stored lower-case).</param>
/// <param name="Province">On an update, null in any of the fields from here on leaves the value unchanged and an empty text clears it, so older clients keep working.</param>
/// <param name="TaxCode">10 digits with an optional -3 digits branch part, or 12 digits.</param>
/// <param name="IdentityNumber">CCCD, 12 digits.</param>
public sealed record SaveSupplierRequest(
    string? Name, SupplierType? SupplierType, string? PhoneNumber, string? Address = null, string? Email = null, string? RowVersion = null,
    string? Province = null, string? Ward = null, string? Note = null, string? CompanyName = null, string? TaxCode = null, string? IdentityNumber = null,
    string? Code = null);

/// <summary>The supplier list and export query. Total purchased and debt parameters are refused without the permission that reads them.</summary>
/// <param name="SortBy">name, code, phone, type, province, createdAt, status, totalPurchased or currentDebt.</param>
/// <param name="SortDir">asc or desc.</param>
public sealed record SupplierListQuery(
    string? Search = null,
    SupplierType? Type = null,
    int? StatusId = null,
    decimal? TotalPurchasedFrom = null,
    decimal? TotalPurchasedTo = null,
    decimal? CurrentDebtFrom = null,
    decimal? CurrentDebtTo = null,
    DateOnly? PurchaseFrom = null,
    DateOnly? PurchaseTo = null,
    string? SortBy = null,
    string? SortDir = null,
    int Page = 1,
    int PageSize = 20);

/// <param name="TotalPurchased">Null without PURCHASE_RECEIPTS View.</param>
/// <param name="CurrentDebt">Null without SUPPLIER_PAYMENTS View (BR-FARM-07).</param>
public sealed record SupplierListItem(
    Guid Id,
    string Name,
    SupplierType SupplierType,
    string PhoneNumber,
    int StatusId,
    string Status,
    string Code,
    string? Email,
    string? Address,
    string? Province,
    string? Ward,
    string? CompanyName,
    string? TaxCode,
    string? Note,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    decimal? TotalPurchased,
    decimal? CurrentDebt);

public sealed record SupplierCertificate(int Index, string Url);

/// <summary>One of the latest purchase receipts of the supplier (any status), with its total (BR-PURCHASE-02).</summary>
public sealed record SupplierReceiptRow(Guid Id, string Code, DateOnly ReceiptDate, int StatusId, string Status, decimal Total);

/// <summary>
/// A supplier with its certificates and its latest receipts. The supplier debt is not here: it is PURCHASE-04, which the
/// Zone Manager does not hold (§I.4.4); it is read from the supplier debt endpoints.
/// </summary>
/// <param name="IdentityNumber">Personal data: on this detail only, never in a list or export.</param>
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
    string RowVersion,
    string Code,
    string? Province,
    string? Ward,
    string? Note,
    string? CompanyName,
    string? TaxCode,
    string? IdentityNumber,
    DateTimeOffset CreatedAt,
    string? CreatedBy);

/// <summary>A purchase receipt or a return of the supplier.</summary>
/// <param name="ReceiptId">The receipt to open: the row itself, or the receipt a return was made from.</param>
public sealed record SupplierHistoryItem(
    Guid Id, Guid? ReceiptId, string Kind, string Code, DateOnly Date, DateTimeOffset CreatedAt, string? CreatedBy, decimal Total, int StatusId, string Status);

public enum SupplierBulkAction
{
    Discontinue,
    Resume,
}

public sealed record SupplierBulkStatusRequest(IReadOnlyList<Guid>? Ids, SupplierBulkAction? Action);

/// <param name="Code">The message code of a refusal; null when it succeeded.</param>
public sealed record SupplierBulkOutcome(Guid Id, bool Succeeded, string? Code, string? Message);

public sealed record SupplierBulkResult(int Succeeded, int Failed, IReadOnlyList<SupplierBulkOutcome> Outcomes);

/// <param name="Row">The row number in the file.</param>
/// <param name="Fields">The invalid columns.</param>
public sealed record SupplierImportError(int Row, IReadOnlyList<string> Fields, string Message);

/// <summary>All-or-nothing: <paramref name="Imported"/> is zero whenever <paramref name="Errors"/> has a row.</summary>
public sealed record SupplierImportResult(int Imported, IReadOnlyList<SupplierImportError> Errors);
