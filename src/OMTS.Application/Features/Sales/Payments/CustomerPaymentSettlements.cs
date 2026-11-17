using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Sales.Customers;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Payments;

/// <summary>A pay back, checked: the money as a payment would be recorded, and the bank account it leaves from (null for cash).</summary>
public sealed record PlannedPayBack(RecordCustomerPaymentRequest Money, Guid? CashAccountId, string? BankReference);

/// <summary>A checked settlement, not applied yet: the profile and the parts of the invoices it pays, or the pay back.</summary>
public sealed record PlannedSettlement(
    CustomerPayment Payment, Customer? Customer, IReadOnlyList<(Guid InvoiceId, decimal Amount)> Parts, PlannedPayBack? PayBack);

public sealed record SettlementPlan(IReadOnlyList<PlannedSettlement> Items);

/// <summary>A settlement applied: <paramref name="PaidBack"/> is null when the payment went to a profile, <paramref name="Customer"/> null when it was paid back.</summary>
public sealed record AppliedSettlement(CustomerPayment Payment, CustomerPayment? PaidBack, Customer? Customer);

/// <summary>
/// DEBT-02.6 – settle a payment without a customer (a transfer whose code matches no saved invoice, held Awaiting confirmation): attach it to the customer profile of the trader who
/// paid, or pay all of it back. It is part of the list of confirmations, in the caller's unit of work, so one save and one notice cover everything (BR-DEBT-06). The caller reads the
/// payments tracked and checks they are Awaiting confirmation.
/// Attached, the payment is Completed and set against the invoices of the profile like any other payment (the invoices the user chooses, otherwise the oldest first, see
/// <see cref="CustomerPaymentAllocationParts"/>), so it counts in the debt and, with the bank account it arrived in, in the cash book. Paid back, the payment is Completed with no customer and
/// money paid back is recorded for all of it (cash, or a transfer from a bank account of the cash book), dated not before the transfer; both lines are in the cash book and the debt does not move.
/// </summary>
public sealed class CustomerPaymentSettlements(
    ICustomerPaymentRepository payments,
    ICustomerRepository customers,
    ICashAccountRepository cashAccounts,
    CustomerDebtReader debts,
    CustomerPaymentCodeService codes,
    CustomerPaymentStatusChanges statusChanges,
    AuditLogService audit,
    IStatusCatalog statusCatalog,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public const string AttachedAction = "AttachedToCustomer";
    public const string PaidBackAction = "PaidBack";

    /// <summary>Every check of DEBT-02.6, nothing changed yet: the first refusal answers, naming the payment.</summary>
    public async Task<Result<SettlementPlan>> PlanAsync(
        IReadOnlyList<SettleInConfirmations> settles, IReadOnlyDictionary<Guid, CustomerPayment> found, CancellationToken cancellationToken)
    {
        var items = new List<PlannedSettlement>();
        var readings = new Dictionary<Guid, CustomerDebtAllocation>();
        var referencesOfTheList = new Dictionary<string, (CustomerPayment PaidBack, Guid PaymentId)>(StringComparer.OrdinalIgnoreCase);
        foreach (var settle in settles)
        {
            var payment = found[settle.Id];
            if (!CustomerPaymentSettlementRules.NeedsSettlement(payment))
            {
                return Named(AppError.Conflict(UserMessages.WrongStatus($"Customer payment {payment.Code}", CustomerPaymentSettlementRules.NotMarkedStatus)), payment.Id);
            }

            var planned = settle.CustomerId is not null
                ? await PlanAttachAsync(settle, payment, readings, cancellationToken)
                : await PlanPayBackAsync(settle.PayBack!, payment, referencesOfTheList, cancellationToken);
            if (!planned.IsSuccess)
            {
                return planned.Error!;
            }

            items.Add(planned.Value!);
        }

        return new SettlementPlan(items);
    }

    /// <summary>
    /// The profile must exist and not be merged; the parts are the ones sent (each an unpaid invoice of the profile) or the oldest unpaid invoices up to the amount. The profile is read once
    /// for the list, and each payment attached to it takes what it pays off that reading, so two payments of one list cannot pay one invoice twice.
    /// </summary>
    private async Task<Result<PlannedSettlement>> PlanAttachAsync(
        SettleInConfirmations settle, CustomerPayment payment, Dictionary<Guid, CustomerDebtAllocation> readings, CancellationToken cancellationToken)
    {
        if (await customers.GetForEditAsync(settle.CustomerId!.Value, cancellationToken) is not { } customer)
        {
            return Named(AppError.InvalidFields(["Customer"]), payment.Id);
        }

        if (customer.MergedIntoCustomerId is not null)
        {
            return Named(AppError.Conflict(UserMessages.WrongStatus($"{CustomerService.ObjectName} {customer.Name}", CustomerService.MergedStatus)), payment.Id);
        }

        var invalid = CustomerPaymentRules.InvalidAllocations(settle.Allocations, payment.Amount);
        if (invalid.Count > 0)
        {
            return Named(AppError.InvalidFields(invalid), payment.Id);
        }

        if (!readings.TryGetValue(customer.Id, out var reading))
        {
            reading = await debts.AllocationAsync(customer.Id, cancellationToken);
        }

        var parts = CustomerPaymentAllocationParts.Of(settle.Allocations, payment.Amount, reading, oldestFirstWhenNone: true, out var error);
        if (error is not null)
        {
            return Named(error, payment.Id);
        }

        readings[customer.Id] = CustomerPaymentAllocationParts.After(reading, parts, payment.Amount);
        return new PlannedSettlement(payment, customer, parts, null);
    }

    /// <summary>
    /// The fields as for money paid back that the Accountant records (BR-DEBT-03), and the date not before the transfer. A transfer leaves from the bank account chosen (an active Bank
    /// account of the cash book) or, left out, from the one the transfer arrived in. MSG-DEBT-05 when the bank reference is already on a payment that is not cancelled, or on an
    /// earlier pay back of the same list.
    /// </summary>
    private async Task<Result<PlannedSettlement>> PlanPayBackAsync(
        SettlementPayBack back, CustomerPayment payment, Dictionary<string, (CustomerPayment PaidBack, Guid PaymentId)> referencesOfTheList, CancellationToken cancellationToken)
    {
        var money = new RecordCustomerPaymentRequest(null, back.PaymentDate, payment.Amount, back.Method, back.BankReference, Note: back.Note, CashAccountId: back.CashAccountId);
        var invalid = CustomerPaymentRules.InvalidFields(money, timeProvider.GetUtcNow(), CustomerPaymentKind.PaidBack);
        if (CustomerPaymentSettlementRules.PaidBackBeforeTransfer(back.PaymentDate, payment))
        {
            invalid.Add("Payment date");
        }

        var cashAccountId = await PaidFromAsync(back, payment, invalid, cancellationToken);
        if (invalid.Count > 0)
        {
            return Named(AppError.InvalidFields(invalid.Distinct().ToList()), payment.Id);
        }

        var reference = back.Method == CustomerPaymentMethod.OtherTransfer ? CustomerPaymentRules.Trimmed(back.BankReference) : null;
        if (reference is not null && !back.ConfirmDuplicateBankReference)
        {
            if (await payments.FindNotCancelledByBankReferenceAsync(reference, cancellationToken) is { } existing)
            {
                return Named(CustomerPaymentRules.DuplicateBankReferenceQuestion(existing), payment.Id, "settlementPaymentId");
            }

            if (referencesOfTheList.TryGetValue(reference, out var earlier))
            {
                return Named(Named(CustomerPaymentRules.DuplicateBankReferenceQuestion(earlier.PaidBack), earlier.PaymentId), payment.Id, "settlementPaymentId");
            }
        }

        if (reference is not null)
        {
            // The money paid back of this entry, as the question names it to a later entry of the list with the same reference (BR-DEBT-03).
            referencesOfTheList.TryAdd(reference, (new CustomerPayment { Code = payment.Code, Amount = payment.Amount, PaymentDate = back.PaymentDate!.Value, BankReference = reference }, payment.Id));
        }

        return new PlannedSettlement(payment, null, [], new PlannedPayBack(money, cashAccountId, reference));
    }

    /// <summary>The bank account a transfer is paid back from; cash has none, so a bank account sent with cash is invalid ("Cash account" is added to <paramref name="invalid"/>).</summary>
    private async Task<Guid?> PaidFromAsync(SettlementPayBack back, CustomerPayment payment, List<string> invalid, CancellationToken cancellationToken)
    {
        if (back.Method != CustomerPaymentMethod.OtherTransfer)
        {
            if (back.CashAccountId is not null && back.Method is not null)
            {
                invalid.Add("Cash account");
            }

            return null;
        }

        if (back.CashAccountId is not { } chosen)
        {
            return payment.CashAccountId;
        }

        var account = await cashAccounts.GetForEditAsync(chosen, cancellationToken);
        if (account is null || account.Kind != CashAccountKind.Bank || !account.IsActive)
        {
            invalid.Add("Cash account");
            return null;
        }

        return account.Id;
    }

    /// <summary>DEBT-02.6 step 4 for each settlement of the plan.</summary>
    public async Task<IReadOnlyList<AppliedSettlement>> ApplyAsync(SettlementPlan plan, CancellationToken cancellationToken)
    {
        var applied = new List<AppliedSettlement>(plan.Items.Count);
        foreach (var item in plan.Items)
        {
            applied.Add(item.Customer is { } customer ? Attach(item, customer) : await PayBackAsync(item, cancellationToken));
        }

        return applied;
    }

    private AppliedSettlement Attach(PlannedSettlement item, Customer customer)
    {
        var payment = item.Payment;
        payment.CustomerId = customer.Id;
        payment.Customer = customer;
        foreach (var (invoiceId, amount) in item.Parts)
        {
            payments.AddAllocation(new CustomerPaymentAllocation { PaymentId = payment.Id, InvoiceId = invoiceId, Amount = amount });
        }

        statusChanges.Settle(payment, AttachedAction, null, item.Parts);
        return new AppliedSettlement(payment, null, customer);
    }

    private async Task<AppliedSettlement> PayBackAsync(PlannedSettlement item, CancellationToken cancellationToken)
    {
        var plan = item.PayBack!;
        var transfer = item.Payment;
        var now = timeProvider.GetUtcNow();
        var me = currentUser.AccountId!.Value;
        var paidAt = CustomerPaymentSettlementRules.PaidBackAt(CustomerPaymentRules.PaidAt(plan.Money, now), transfer);
        var paidBack = new CustomerPayment
        {
            Code = await codes.NextAsync(cancellationToken),
            Kind = CustomerPaymentKind.PaidBack,
            PaymentDate = BusinessClock.ToBusinessDate(paidAt),
            PaidAt = paidAt,
            Amount = transfer.Amount,
            Method = plan.Money.Method!.Value,
            BankReference = plan.BankReference,
            CashAccountId = plan.CashAccountId,
            CollectedByAccountId = me,
            RecordedByAccountId = me,
            Note = CustomerPaymentRules.Trimmed(plan.Money.Note),
            StatusId = StatusIds.CustomerPayment.Completed,
            ConfirmedByAccountId = me,
            ConfirmedAt = now,
            PaidBackForPaymentId = transfer.Id,
        };
        payments.Add(paidBack);
        audit.Record(new AuditEntry(SalesInvoiceWriter.PaymentRecordedAction, SalesInvoiceWriter.PaymentRecordType)
        {
            RecordId = paidBack.Id,
            RecordCode = paidBack.Code,
            After = new
            {
                paidBack.Kind, paidBack.Amount, paidBack.PaymentDate, paidBack.PaidAt, paidBack.Method, paidBack.BankReference, paidBack.CashAccountId, paidBack.Note,
                paidBack.PaidBackForPaymentId, Status = statusCatalog.CodeOf(paidBack.StatusId),
            },
        });
        statusChanges.Settle(transfer, PaidBackAction, paidBack, []);
        return new AppliedSettlement(transfer, paidBack, null);
    }

    /// <summary>Adds which payment of the list this refusal is about (the question of a bank reference already names the other payment in <c>paymentId</c>).</summary>
    private static AppError Named(AppError error, Guid paymentId, string key = "paymentId") =>
        error with { Details = new Dictionary<string, object?>(error.Details ?? new Dictionary<string, object?>()) { [key] = paymentId } };
}
