using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Payments;

/// <summary>DEBT-02 step 2 (a payment) or DEBT-02.3 step 3 (money paid back); the endpoint says which.</summary>
/// <param name="Method">Cash or OtherTransfer: VietQrInvoice is recorded by the system only (GFR-08).</param>
/// <param name="BankReference">Required for a transfer, ignored for cash.</param>
/// <param name="CollectedByAccountId">A payment in cash: the Zone Manager (or Owner) who handed it over; ignored otherwise and for money paid back.</param>
/// <param name="ConfirmAboveDebt">Yes to MSG-DEBT-04 (payments only).</param>
/// <param name="ConfirmDuplicateBankReference">Yes to MSG-DEBT-05.</param>
/// <param name="DeduplicationKey">A repeated key returns the first payment and creates nothing (NFR-04).</param>
public sealed record RecordCustomerPaymentRequest(
    Guid? CustomerId,
    DateOnly? PaymentDate,
    decimal? Amount,
    CustomerPaymentMethod? Method,
    string? BankReference = null,
    Guid? CollectedByAccountId = null,
    string? Note = null,
    bool ConfirmAboveDebt = false,
    bool ConfirmDuplicateBankReference = false,
    string? DeduplicationKey = null);

/// <summary>DEBT-02.4: the reason is required. <paramref name="RowVersion"/>, when sent, must be the version shown.</summary>
public sealed record CancelCustomerPaymentRequest(string? Reason, string? RowVersion = null);

/// <summary>DEBT-02.2 step 3: the payments whose money arrived, and those cancelled from the list (DEBT-02.4), saved together with one notice.</summary>
public sealed record ConfirmCustomerPaymentsRequest(IReadOnlyList<Guid>? Confirm = null, IReadOnlyList<CancelInConfirmations>? Cancel = null);

public sealed record CancelInConfirmations(Guid Id, string? Reason);

public sealed record CustomerPaymentInvoiceRef(Guid Id, string Code);

/// <summary>A customer payment or money paid back as the staff see it (internal data included: BR-DEBT-04 strips it for the trader only).</summary>
/// <param name="Customer">Null for a payment without a customer profile (BR-DEBT-03).</param>
/// <param name="Invoice">The invoice the money came with (cash at the port, VietQR code of the invoice).</param>
/// <param name="RecordedBy">Null when the system recorded it (GFR-08).</param>
public sealed record CustomerPaymentDetails(
    Guid Id, string Code, CustomerDebtCustomer? Customer, CustomerPaymentKind Kind, decimal Amount, DateOnly PaymentDate, CustomerPaymentMethod Method,
    string? BankReference, CustomerPaymentInvoiceRef? Invoice, AccountRef? CollectedBy, AccountRef? RecordedBy, string? Note,
    int StatusId, string Status, AccountRef? ConfirmedBy, DateTimeOffset? ConfirmedAt, AccountRef? CancelledBy, DateTimeOffset? CancelledAt, string? CancelReason,
    string RowVersion);

/// <summary>The answer of a record, a pay back or a cancel: the payment, the customer debt after it (null without a profile) and MSG-GEN-05.</summary>
/// <param name="DebtAfter">On a repeated deduplication key: the debt now.</param>
public sealed record CustomerPaymentSaved(CustomerPaymentDetails Payment, decimal? DebtAfter, string Message, string MessageCode);

/// <summary>DEBT-02 step 1 and DEBT-02.3 step 2 (SCR-DEBT-03): the debt today, the money owed back, the unpaid invoices oldest first and who can hand over cash.</summary>
/// <param name="OwedBack">The money owed back (positive) when the debt is below 0; the most that can be paid back (BR-DEBT-03).</param>
/// <param name="CashCollectors">The active Zone Managers and Owners: the people who collect cash at the port.</param>
public sealed record CustomerPaymentForm(
    CustomerDebtCustomer Customer, decimal Debt, decimal OwedBack, int? DebtAgeDays, IReadOnlyList<UnpaidInvoice> UnpaidInvoices, IReadOnlyList<AccountRef> CashCollectors);

/// <summary>DEBT-02.2 step 2: oldest first.</summary>
/// <param name="TotalAmount">Σ of every payment Awaiting confirmation, not only of the page.</param>
/// <param name="Message">MSG-GEN-11 when nothing is Awaiting confirmation (DEBT-02.2.E1).</param>
public sealed record AwaitingCustomerPayments(Paged<CustomerPaymentDetails> Page, decimal TotalAmount, string? Message, string? MessageCode);

/// <summary>The answer of a list of confirmations: what was confirmed and cancelled, and MSG-GEN-05.</summary>
public sealed record CustomerPaymentsConfirmed(IReadOnlyList<CustomerPaymentDetails> Confirmed, IReadOnlyList<CustomerPaymentDetails> Cancelled, string Message, string MessageCode);
