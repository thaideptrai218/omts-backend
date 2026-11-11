using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.CashBook;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Payments;

/// <summary>The part of a payment set against one unpaid invoice of the customer: whole VND above zero, at most what the invoice has left.</summary>
public sealed record CustomerPaymentAllocationInput(Guid? InvoiceId, decimal? Amount);

/// <summary>DEBT-02 step 2 (a payment) or DEBT-02.3 step 3 (money paid back); the endpoint says which.</summary>
/// <param name="PaymentDate">The date paid; may be left out when <paramref name="PaidAt"/> is sent, and must then be its date at UTC+7.</param>
/// <param name="Amount">The money received or paid back, whole VND; 0 only on a payment that is all <paramref name="DiscountAmount"/>.</param>
/// <param name="Method">Cash or OtherTransfer: VietQrInvoice is recorded by the system only (GFR-08). Not needed when the amount is 0.</param>
/// <param name="BankReference">Required for a transfer, ignored for cash.</param>
/// <param name="CollectedByAccountId">Who collected the money ("Người thu"), or paid it back: an Active Owner or staff account; the caller by default.</param>
/// <param name="ConfirmAboveDebt">Yes to MSG-DEBT-04 (payments only).</param>
/// <param name="ConfirmDuplicateBankReference">Yes to MSG-DEBT-05.</param>
/// <param name="DeduplicationKey">A repeated key returns the first payment and creates nothing (NFR-04).</param>
/// <param name="PaidAt">When the money was paid, never later than now. Without it: the payment date at the time now when it is today, or at the start of an earlier day.</param>
/// <param name="Code">The code the user types: 1–30 ASCII letters, digits, "-", "_" or "."; unique among customer payments regardless of case. Empty: the next PT code.</param>
/// <param name="DiscountAmount">Debt written off with the payment, with no money received (payments only); it covers invoices like money does.</param>
/// <param name="CashAccountId">A transfer: the Bank account of the cash book the money came into or was paid from. Required to make a VietQR code.</param>
/// <param name="Allocations">
/// Payments only: the unpaid invoices of the customer it pays, one part per invoice, each at most what the invoice has left, together at most the amount plus the discount.
/// Left out: nothing is set against an invoice and the payment covers the oldest invoices (for a payment waiting for a transfer: set oldest first up to the amount).
/// </param>
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
    string? DeduplicationKey = null,
    DateTimeOffset? PaidAt = null,
    string? Code = null,
    decimal DiscountAmount = 0m,
    Guid? CashAccountId = null,
    IReadOnlyList<CustomerPaymentAllocationInput>? Allocations = null);

/// <summary>DEBT-02.4: the reason is required. <paramref name="RowVersion"/>, when sent, must be the version shown.</summary>
public sealed record CancelCustomerPaymentRequest(string? Reason, string? RowVersion = null);

/// <summary>DEBT-02.2 step 3: the payments whose money arrived, and those cancelled from the list (DEBT-02.4), saved together with one notice.</summary>
public sealed record ConfirmCustomerPaymentsRequest(IReadOnlyList<Guid>? Confirm = null, IReadOnlyList<CancelInConfirmations>? Cancel = null);

public sealed record CancelInConfirmations(Guid Id, string? Reason);

public sealed record CustomerPaymentInvoiceRef(Guid Id, string Code);

/// <summary>An invoice a payment pays, with the part set against it.</summary>
public sealed record CustomerPaymentAllocationView(Guid InvoiceId, string InvoiceCode, DateTimeOffset InvoicedAt, decimal InvoiceTotal, decimal Amount);

/// <summary>A customer payment or money paid back as the staff see it (internal data included: BR-DEBT-04 strips it for the trader only).</summary>
/// <param name="Customer">Null for a payment without a customer profile (BR-DEBT-03).</param>
/// <param name="Invoice">The invoice the money came with (cash at the port, VietQR code of the invoice).</param>
/// <param name="CollectedBy">The person who collected the money ("Người thu").</param>
/// <param name="RecordedBy">Null when the system recorded it (GFR-08).</param>
/// <param name="TransferInvoiceCode">A transfer whose invoice code matches no saved invoice (Stop sale, an unknown code): the code it carried (DEBT-02.6).</param>
/// <param name="TransferredAt">The time of the transfer the Payment Notification Service reported (GFR-08).</param>
/// <param name="PaidAt">When the money was paid, at UTC+7; <paramref name="PaymentDate"/> is its date.</param>
/// <param name="DiscountAmount">Debt written off with the payment.</param>
/// <param name="CashAccount">The bank account of a transfer.</param>
/// <param name="Allocations">The invoices it pays, in the order of their invoices; empty when it names none (the oldest invoices are covered).</param>
public sealed record CustomerPaymentDetails(
    Guid Id, string Code, CustomerDebtCustomer? Customer, CustomerPaymentKind Kind, decimal Amount, DateOnly PaymentDate, CustomerPaymentMethod Method,
    string? BankReference, CustomerPaymentInvoiceRef? Invoice, AccountRef? CollectedBy, AccountRef? RecordedBy, string? Note,
    int StatusId, string Status, AccountRef? ConfirmedBy, DateTimeOffset? ConfirmedAt, AccountRef? CancelledBy, DateTimeOffset? CancelledAt, string? CancelReason,
    string RowVersion, string? TransferInvoiceCode = null, DateTimeOffset? TransferredAt = null,
    DateTimeOffset PaidAt = default, decimal DiscountAmount = 0m, CashAccountRef? CashAccount = null, IReadOnlyList<CustomerPaymentAllocationView>? Allocations = null);

/// <summary>The VietQR code of a payment waiting for its transfer: the client draws the image from <paramref name="Payload"/>.</summary>
/// <param name="Payload">The EMVCo text to draw as a QR code.</param>
/// <param name="Content">The transfer content: the payment code, as the code holds it (at most 25 characters).</param>
public sealed record CustomerPaymentQr(string Payload, string BankBin, string BankName, string AccountNumber, string AccountHolderName, decimal Amount, string Content);

/// <summary>The answer of a record, a pay back, a cancel or a QR: the payment, the customer debt after it (null without a profile) and MSG-GEN-05.</summary>
/// <param name="DebtAfter">On a repeated deduplication key: the debt now. Unchanged for a payment waiting for its transfer.</param>
/// <param name="Qr">Only for a payment waiting for its transfer.</param>
public sealed record CustomerPaymentSaved(CustomerPaymentDetails Payment, decimal? DebtAfter, string Message, string MessageCode, CustomerPaymentQr? Qr = null);

/// <summary>A bank account the VietQR code of a payment may pay into: a Bank account of the cash book.</summary>
/// <param name="IsDefault">The receiving bank account of the cooperative, the one the invoices' VietQR codes pay into.</param>
public sealed record CustomerPaymentBankAccount(Guid Id, string BankBin, string ProviderName, string AccountNumber, string HolderName, bool IsDefault);

/// <summary>DEBT-02 step 1 and DEBT-02.3 step 2 (SCR-DEBT-03): the debt today, the money owed back, the unpaid invoices oldest first and who can collect.</summary>
/// <param name="OwedBack">The money owed back (positive) when the debt is below 0; the most that can be paid back (BR-DEBT-03).</param>
/// <param name="CashCollectors">The active Owner and staff accounts: who may be named as having collected the money or paid it back.</param>
/// <param name="BankAccounts">The Bank accounts of the cash book a VietQR code may pay into.</param>
public sealed record CustomerPaymentForm(
    CustomerDebtCustomer Customer, decimal Debt, decimal OwedBack, int? DebtAgeDays, IReadOnlyList<UnpaidInvoice> UnpaidInvoices, IReadOnlyList<AccountRef> CashCollectors,
    IReadOnlyList<CustomerPaymentBankAccount> BankAccounts);

/// <summary>DEBT-02.2 step 2: oldest first.</summary>
/// <param name="TotalAmount">Σ of every payment Awaiting confirmation, not only of the page.</param>
/// <param name="Message">MSG-GEN-11 when nothing is Awaiting confirmation (DEBT-02.2.E1).</param>
public sealed record AwaitingCustomerPayments(Paged<CustomerPaymentDetails> Page, decimal TotalAmount, string? Message, string? MessageCode);

/// <summary>The answer of a list of confirmations: what was confirmed and cancelled, and MSG-GEN-05.</summary>
public sealed record CustomerPaymentsConfirmed(IReadOnlyList<CustomerPaymentDetails> Confirmed, IReadOnlyList<CustomerPaymentDetails> Cancelled, string Message, string MessageCode);

/// <summary>A debt adjustment: above 0 the customer owes more, below 0 less.</summary>
/// <param name="Amount">Signed whole VND, never 0.</param>
/// <param name="OccurredAt">When it takes effect, never later than now; now when left out.</param>
public sealed record RecordCustomerDebtAdjustmentRequest(Guid? CustomerId, decimal? Amount, string? Reason, DateTimeOffset? OccurredAt = null);

public sealed record CustomerDebtAdjustmentView(
    Guid Id, string Code, CustomerDebtCustomer Customer, decimal Amount, DateTimeOffset OccurredAt, DateOnly AdjustmentDate, string Reason, AccountRef? RecordedBy,
    bool Cancelled, DateTimeOffset? CancelledAt, string? CancelReason, string RowVersion);

/// <summary>The answer of an adjustment or its cancellation: the adjustment, the debt after it and MSG-GEN-05.</summary>
public sealed record CustomerDebtAdjustmentSaved(CustomerDebtAdjustmentView Adjustment, decimal DebtAfter, string Message, string MessageCode);
