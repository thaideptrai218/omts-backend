using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.CashBook;

/// <summary>Where a line of the cash book comes from: a self-made voucher, or a supplier payment or money a supplier paid back.</summary>
public enum CashSource
{
    CashVoucher,
    SupplierPayment,
    SupplierPaidBack,
}

/// <summary>A voucher counts while Recorded; a cancelled one stays listed but never counts in a balance.</summary>
public enum CashEntryStatus
{
    Recorded,
    Cancelled,
}

/// <summary>The fund a list reads: one of the three, or all together.</summary>
public enum CashFundFilter
{
    Cash,
    Bank,
    EWallet,
    All,
}

public enum CashBookSort
{
    Code,
    Time,
}

public sealed record CashAccountRef(Guid Id, string ProviderName, string AccountNumber);

public sealed record CashTypeRef(Guid Id, string Name, CashCategory Category);

/// <param name="Id">Null for a party named on the voucher that no record backs any more.</param>
public sealed record CashPartyRef(CashPartyKind Kind, Guid? Id, string? Code, string Name, string? PhoneNumber);

/// <param name="Kind">"SupplierPayment" or "PurchaseReturn" (the return that money paid back by a supplier was for).</param>
public sealed record CashSourceDocument(string Kind, Guid Id, string Code);

// ---- Cash accounts ----

/// <param name="ProviderName">The short name of the bank from the bank list (Bank), or the wallet provider as typed (EWallet).</param>
/// <param name="IsVietQr">Same bank and number as the cooperative's receiving bank account, the one the VietQR codes pay into.</param>
public sealed record CashAccountView(
    Guid Id, CashAccountKind Kind, string? BankBin, string ProviderName, string AccountNumber, string HolderName, string? Note,
    bool IsActive, bool IsVietQr, string RowVersion);

/// <param name="BankBin">Required for a Bank account: a bank of the bank list, which gives the provider name.</param>
/// <param name="ProviderName">Required for an EWallet; ignored for a Bank account.</param>
/// <param name="IsActive">An edit only; a new account is active.</param>
/// <param name="RowVersion">An edit only.</param>
public sealed record SaveCashAccountRequest(
    CashAccountKind? Kind, string? BankBin, string? ProviderName, string? AccountNumber, string? HolderName, string? Note = null,
    bool IsActive = true, string? RowVersion = null);

// ---- Voucher types ----

public sealed record CashVoucherTypeView(
    Guid Id, CashDirection Direction, CashCategory Category, string Name, string? Description, bool CountsInResult, bool IsSystem, bool Manual,
    string RowVersion);

public sealed record SaveCashVoucherTypeRequest(
    CashDirection? Direction, CashCategory? Category, string? Name, string? Description = null, bool CountsInResult = false, string? RowVersion = null);

// ---- Parties and collectors ----

public sealed record CashPartyView(Guid Id, CashPartyKind Kind, string? Code, string Name, string? PhoneNumber);

public sealed record CreateCashPartyRequest(string? Name, string? PhoneNumber = null, string? Address = null, string? Area = null, string? Ward = null, string? Note = null);

// ---- Vouchers ----

/// <param name="Code">Empty: the next code of the sequence of the fund and direction; otherwise a code the user types, 1 to 30 letters, digits, "-", "_" or ".".</param>
/// <param name="CashAccountId">A Bank or EWallet voucher names an active account of that kind; a Cash voucher none.</param>
/// <param name="TypeId">A type that may be chosen by hand, of the direction of the voucher.</param>
/// <param name="OccurredAt">When the money moved, never later than now.</param>
/// <param name="CollectorId">Who collected or paid: an Active Owner or staff account.</param>
/// <param name="PartyKind">With <paramref name="PartyId"/>: who paid or received; both empty for no party. The name is read from the record.</param>
/// <param name="PartyName">Not read: the name is the one of the record named.</param>
/// <param name="CountsInResult">Left out: the value of the type.</param>
/// <param name="RowVersion">An edit only.</param>
public sealed record SaveCashVoucherRequest(
    string? Code,
    CashDirection? Direction,
    CashFund? Fund,
    Guid? CashAccountId,
    Guid? TypeId,
    DateTimeOffset? OccurredAt,
    decimal? Amount,
    Guid? CollectorId = null,
    CashPartyKind? PartyKind = null,
    Guid? PartyId = null,
    string? PartyName = null,
    string? Note = null,
    bool? CountsInResult = null,
    string? RowVersion = null);

public sealed record CancelCashVoucherRequest(string? Reason, string? RowVersion = null);

public sealed record CashVoucherView(
    Guid Id,
    string Code,
    CashDirection Direction,
    CashFund Fund,
    CashAccountRef? CashAccount,
    CashTypeRef Type,
    DateTimeOffset OccurredAt,
    decimal Amount,
    AccountRef? Collector,
    CashPartyRef? Party,
    string? Note,
    bool CountsInResult,
    CashEntryStatus Status,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    DateTimeOffset CreatedAt,
    AccountRef? CreatedBy,
    DateTimeOffset? UpdatedAt,
    string RowVersion);

// ---- The book ----

/// <summary>The filters of the list and of the export; paging is for the list only. The period is on the business date.</summary>
/// <param name="Status">Left out: both Recorded and Cancelled lines.</param>
/// <param name="Party">Text in the name of the party.</param>
/// <param name="Search">Text in the code.</param>
public sealed record CashBookQuery(
    CashFundFilter Fund = CashFundFilter.Cash,
    Guid? CashAccountId = null,
    DateOnly? From = null,
    DateOnly? To = null,
    CashDirection? Direction = null,
    Guid? TypeId = null,
    CashEntryStatus? Status = null,
    Guid? CreatedById = null,
    CashPartyKind? PartyKind = null,
    string? Party = null,
    bool? CountsInResult = null,
    string? Search = null,
    CashBookSort Sort = CashBookSort.Code,
    bool Desc = true,
    int Page = 1,
    int PageSize = 15);

public sealed record CashBookRow(
    Guid Id,
    CashSource Source,
    string Code,
    DateTimeOffset OccurredAt,
    DateTimeOffset CreatedAt,
    CashDirection Direction,
    CashFund Fund,
    CashAccountRef? CashAccount,
    CashTypeRef Type,
    CashPartyRef? Party,
    decimal Amount,
    decimal SignedAmount,
    AccountRef? Collector,
    AccountRef? CreatedBy,
    string? Note,
    bool CountsInResult,
    CashEntryStatus Status,
    CashSourceDocument? SourceDocument);

/// <param name="Opening">The balance at the start of the period: what the Recorded lines before it leave; 0 without a start.</param>
public sealed record CashBookSummary(decimal Opening, decimal TotalIn, decimal TotalOut, decimal Closing)
{
    /// <summary>The summary from the sums of the Recorded lines: receipts and payments before the period, and within it.</summary>
    public static CashBookSummary Of(decimal receiptsBefore, decimal paymentsBefore, decimal receipts, decimal payments)
    {
        var opening = receiptsBefore - paymentsBefore;
        return new CashBookSummary(opening, receipts, payments, opening + receipts - payments);
    }
}

public sealed record CashBookPage(IReadOnlyList<CashBookRow> Items, int Page, int PageSize, int Total, CashBookSummary Summary);
