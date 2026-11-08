using OMTS.Application.Common;
using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Sales.Customers;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Payments;

/// <summary>
/// DEBT-02 – the Accountant (or the Owner) records money received from a customer (step 6) or paid back to one (DEBT-02.3), both Confirmed when saved. Each save holds
/// the payment, its audit row and the Owner's MSG-DEBT-03 (BR-DEBT-06). Payments have no zone: the API checks the role only (BR-FARM-08). The customer profile is part
/// of each save and is read before the debt, so of two saves that read one debt only the first succeeds and the other answers MSG06: the debt told to the Owner and in
/// MSG-DEBT-04/06 rests on a true reading (as the supplier payments). Confirmations and cancellations: <see cref="CustomerPaymentStatusService"/>.
/// </summary>
public sealed class CustomerPaymentService(
    ICustomerPaymentRepository payments,
    ICustomerRepository customers,
    CustomerDebtReader debts,
    CustomerPaymentNotifications notifications,
    CustomerPaymentViews views,
    IZoneRecipientQuery recipients,
    IAccountRepository accounts,
    IDocumentNumberAllocator numbers,
    ICurrentUser currentUser,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>DEBT-02 step 1 / DEBT-02.3 step 2 for any profile; 404 for an unknown one.</summary>
    public async Task<Result<CustomerPaymentForm>> FormAsync(Guid customerId, CancellationToken cancellationToken)
    {
        if (await customers.GetForEditAsync(customerId, cancellationToken) is not { } customer)
        {
            return AppError.NotFound;
        }

        var allocation = await debts.AllocationAsync(customerId, cancellationToken);
        var collectorIds = await CashCollectorsAsync(cancellationToken);
        var names = await accounts.GetDisplayNamesAsync(collectorIds, cancellationToken);
        var collectors = collectorIds
            .Select(id => new AccountRef(id, names.GetValueOrDefault(id) ?? string.Empty))
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .ToList();
        return new CustomerPaymentForm(
            new CustomerDebtCustomer(customer.Id, customer.Name, customer.PhoneNumber), allocation.Debt, CustomerDebtMath.OwedBack(allocation.Debt),
            allocation.DebtAgeDays, allocation.UnpaidInvoices, collectors);
    }

    public async Task<Result<CustomerPaymentDetails>> GetAsync(Guid paymentId, CancellationToken cancellationToken) =>
        await payments.GetAsync(paymentId, cancellationToken) is { } payment ? await views.ViewAsync(payment, cancellationToken) : AppError.NotFound;

    /// <summary>
    /// DEBT-02 steps 2-7 (<paramref name="kind"/> Payment) or DEBT-02.3 (PaidBack). One question at a time, answered by sending its flag with the same request:
    /// MSG-DEBT-04 (payments only), then MSG-DEBT-05. Money paid back above the money owed back is refused (MSG-DEBT-06).
    /// </summary>
    public async Task<Result<CustomerPaymentSaved>> RecordAsync(RecordCustomerPaymentRequest request, CustomerPaymentKind kind, CancellationToken cancellationToken)
    {
        var key = CustomerPaymentRules.Trimmed(request.DeduplicationKey);
        if (key is not null && await FirstOfKeyAsync(key, cancellationToken) is { } first)
        {
            return first;
        }

        var invalid = CustomerPaymentRules.InvalidFields(request, timeProvider.Today());
        var customer = request.CustomerId is { } customerId ? await customers.GetForEditAsync(customerId, cancellationToken) : null;
        if (customer is null)
        {
            invalid.Insert(0, "Customer");
        }

        // Cash received (DEBT-02 step 2) names the Zone Manager who handed it over; the Accountant pays money back in person.
        var cashHandedOver = kind == CustomerPaymentKind.Payment && request.Method == CustomerPaymentMethod.Cash;
        if (cashHandedOver && (request.CollectedByAccountId is not { } collector || !(await CashCollectorsAsync(cancellationToken)).Contains(collector)))
        {
            invalid.Add("Zone Manager");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (customer!.MergedIntoCustomerId is not null)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"{CustomerService.ObjectName} {customer.Name}", CustomerService.MergedStatus));
        }

        var amount = request.Amount!.Value;
        var debt = await debts.DebtAsync(customer.Id, cancellationToken);
        var refused = kind == CustomerPaymentKind.Payment
            ? CustomerPaymentRules.AboveDebtQuestion(customer.Name, debt, amount, request.ConfirmAboveDebt)
            : CustomerPaymentRules.PaidBackAboveOwedBack(customer.Name, debt, amount);
        if (refused is not null)
        {
            return refused;
        }

        var bankReference = request.Method == CustomerPaymentMethod.OtherTransfer ? CustomerPaymentRules.Trimmed(request.BankReference) : null;
        if (bankReference is not null && !request.ConfirmDuplicateBankReference
            && await payments.FindNotCancelledByBankReferenceAsync(bankReference, cancellationToken) is { } existing)
        {
            return CustomerPaymentRules.DuplicateBankReferenceQuestion(existing);
        }

        var me = currentUser.AccountId!.Value;
        // Taken once the payment is valid and confirmed, so a refused request spends no number; never given twice.
        var payment = new CustomerPayment
        {
            Code = SalesDocumentCodes.PaymentCode(await numbers.NextAsync(DocumentSequenceKeys.CustomerPayment, cancellationToken)),
            CustomerId = customer.Id,
            Customer = customer,
            Kind = kind,
            PaymentDate = request.PaymentDate!.Value,
            Amount = amount,
            Method = request.Method!.Value,
            BankReference = bankReference,
            CollectedByAccountId = cashHandedOver ? request.CollectedByAccountId : null,
            RecordedByAccountId = me,
            Note = CustomerPaymentRules.Trimmed(request.Note),
            DeduplicationKey = key,
            // BR-DEBT-03: the Accountant records only money that has reached the cooperative (or left it), so it is Confirmed at once.
            StatusId = StatusIds.CustomerPayment.Confirmed,
            ConfirmedByAccountId = me,
            ConfirmedAt = timeProvider.GetUtcNow(),
        };
        payments.Add(payment);
        unitOfWork.MarkChanged(customer);
        audit.Record(new AuditEntry(SalesInvoiceWriter.PaymentRecordedAction, SalesInvoiceWriter.PaymentRecordType)
        {
            RecordId = payment.Id,
            RecordCode = payment.Code,
            After = new
            {
                payment.Kind, payment.CustomerId, payment.Amount, payment.PaymentDate, payment.Method, payment.BankReference, payment.CollectedByAccountId, payment.Note,
                Status = statusCatalog.CodeOf(payment.StatusId),
            },
        });
        var debtAfter = CustomerPaymentRules.DebtAfter(debt, kind, amount, cancelled: false);
        await notifications.TransactionAsync(payment, cancelled: false, debtAfter, cancellationToken);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (key is not null && exception is DuplicateRecordException or ConcurrencyConflictException)
        {
            // A parallel request with the same key was saved first: this one created nothing. With no payment of the key, another save won: MSG06, the key stays free.
            if (await FirstOfKeyAsync(key, cancellationToken) is { } saved)
            {
                return saved;
            }

            throw;
        }

        return await views.SavedAsync(payment, debtAfter, cancelled: false, cancellationToken);
    }

    /// <summary>The Owners and the Zone Managers with an Effective pair: who collects cash at the port and hands it to the Accountant (BR-SALE-07 (3)).</summary>
    private Task<IReadOnlyList<Guid>> CashCollectorsAsync(CancellationToken cancellationToken) =>
        recipients.GetOwnersAndRolesAsync([StaffRole.ZoneManager], cancellationToken);

    private async Task<CustomerPaymentSaved?> FirstOfKeyAsync(string key, CancellationToken cancellationToken)
    {
        if (await payments.FindByDeduplicationKeyAsync(key, cancellationToken) is not { } first)
        {
            return null;
        }

        decimal? debt = first.CustomerId is { } customerId ? await debts.DebtAsync(customerId, cancellationToken) : null;
        return await views.SavedAsync(first, debt, cancelled: false, cancellationToken);
    }
}
