using OMTS.Application.Common;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Suppliers;

/// <param name="Address">The street of the contact address (house number and street name); optional, at most 500 characters. <paramref name="Street"/> is the same entry under the name of the address picker.</param>
/// <param name="Email">Optional; a valid address when entered (stored lower-case).</param>
/// <param name="Province">
/// The name of the contact province; with <paramref name="ProvinceCode"/>, a province of the administrative units list. <paramref name="ProvinceName"/> is the same entry.
/// On an update, when none of the province and ward entries (name or code) is sent they stay as they are; when any is sent the contact province and ward are replaced by what is sent.
/// For the fields from here on, null leaves the value unchanged and an empty text clears it, so older clients keep working.
/// </param>
/// <param name="Ward">The name of the contact ward; with <paramref name="WardCode"/>, a ward of that province. <paramref name="WardName"/> is the same entry.</param>
/// <param name="CompanyName">Without <paramref name="InvoiceBuyerType"/>: the company name, as an older client sends it. With it, the invoice name is <paramref name="InvoiceName"/> (or this entry when that is empty).</param>
/// <param name="TaxCode">Organization only: 10 digits with an optional -3 digits branch part. Without <paramref name="InvoiceBuyerType"/> an older client may send 12 digits too.</param>
/// <param name="InvoiceBuyerType">
/// Organization or Individual (never Consumer). Sent, it replaces the whole invoice info: Organization needs the invoice name, tax code, email and address; Individual the name and address.
/// Left out, the invoice info of an update stays as it is and is not checked (older clients); a create then holds Organization when a tax code is sent, else Individual.
/// </param>
/// <param name="InvoiceName">The company name of an Organization, the person's name of an Individual.</param>
/// <param name="PersonalTaxId">Individual only: the personal tax number (10 digits) or ID number (12 digits), optional. Personal data: on the detail only, never in a list, an export or the audit log.</param>
/// <param name="InvoiceAddressSameAsContact">True (the default) when the invoice address is the contact address, which must then be complete.</param>
/// <param name="InvoiceEmail">Required for an Organization, optional for an Individual.</param>
public sealed record SaveSupplierRequest(
    string? Name, SupplierType? SupplierType, string? PhoneNumber, string? Address = null, string? Email = null, string? RowVersion = null,
    string? Province = null, string? Ward = null, string? Note = null, string? CompanyName = null, string? TaxCode = null,
    string? Code = null,
    int? ProvinceCode = null,
    int? WardCode = null,
    InvoiceBuyerType? InvoiceBuyerType = null,
    string? InvoiceName = null,
    string? PersonalTaxId = null,
    bool? InvoiceAddressSameAsContact = null,
    int? InvoiceProvinceCode = null,
    string? InvoiceProvinceName = null,
    int? InvoiceWardCode = null,
    string? InvoiceWardName = null,
    string? InvoiceStreet = null,
    string? InvoiceEmail = null,
    string? ProvinceName = null,
    string? WardName = null,
    string? Street = null);

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
/// <param name="CompanyName">The company name of an Organization; null for an Individual (<paramref name="InvoiceName"/> holds the name of either).</param>
/// <param name="Street">The same as <paramref name="Address"/>, as the address picker names it; <paramref name="ProvinceName"/> and <paramref name="WardName"/> likewise repeat <paramref name="Province"/> and <paramref name="Ward"/>.</param>
/// <param name="InvoiceAddressSameAsContact">When true the invoice address fields are empty and the contact address is the invoice address.</param>
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
    decimal? CurrentDebt,
    int? ProvinceCode,
    int? WardCode,
    string? ProvinceName,
    string? WardName,
    string? Street,
    InvoiceBuyerType InvoiceBuyerType,
    string? InvoiceName,
    bool InvoiceAddressSameAsContact,
    int? InvoiceProvinceCode,
    string? InvoiceProvinceName,
    int? InvoiceWardCode,
    string? InvoiceWardName,
    string? InvoiceStreet,
    string? InvoiceEmail);

public sealed record SupplierCertificate(int Index, string Url);

/// <summary>One of the latest purchase receipts of the supplier (any status), with its total (BR-PURCHASE-02).</summary>
public sealed record SupplierReceiptRow(Guid Id, string Code, DateOnly ReceiptDate, int StatusId, string Status, decimal Total);

/// <summary>
/// A supplier with its certificates and its latest receipts. The supplier debt is not here: it is PURCHASE-04, which the
/// Zone Manager does not hold (§I.4.4); it is read from the supplier debt endpoints.
/// </summary>
/// <param name="PersonalTaxId">Personal data: on this detail only, never in a list or export.</param>
/// <param name="CompanyName">The company name of an Organization; null for an Individual (<paramref name="InvoiceName"/> holds the name of either).</param>
/// <param name="Street">The same as <paramref name="Address"/>, as the address picker names it; <paramref name="ProvinceName"/> and <paramref name="WardName"/> likewise repeat <paramref name="Province"/> and <paramref name="Ward"/>.</param>
/// <param name="InvoiceAddressSameAsContact">When true the invoice address fields are empty and the contact address is the invoice address.</param>
/// <param name="AddressNeedsReview">An address name (contact, or the invoice address when it differs) is kept as text without a code of the administrative units list: pick it again.</param>
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
    string? PersonalTaxId,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    int? ProvinceCode,
    int? WardCode,
    string? ProvinceName,
    string? WardName,
    string? Street,
    InvoiceBuyerType InvoiceBuyerType,
    string? InvoiceName,
    bool InvoiceAddressSameAsContact,
    int? InvoiceProvinceCode,
    string? InvoiceProvinceName,
    int? InvoiceWardCode,
    string? InvoiceWardName,
    string? InvoiceStreet,
    string? InvoiceEmail,
    bool AddressNeedsReview);

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
