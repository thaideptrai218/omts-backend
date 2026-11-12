using OMTS.Application.Common;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.CashBook;
using OMTS.Application.Features.Purchasing;
using OMTS.Application.Features.ReceivingBankAccounts;
using OMTS.Application.Features.Sales.Customers;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.CashBook;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Payments;

/// <summary>
/// DEBT-02 – the Accountant (or the Owner) records money received from a customer (step 6) or paid back to one (DEBT-02.3), both Completed when saved, or makes the VietQR code of a
/// payment that waits for the customer's transfer (Pending transfer: it counts nowhere until the transfer arrives, see <see cref="BankTransferNoticeService"/>). A payment may name the
/// invoices it pays and a discount; what it does not name covers the oldest invoices (<see cref="CustomerDebtAllocator"/>). Each save holds the payment, its audit row and the
/// Owner's MSG-DEBT-03 (BR-DEBT-06). Payments have no zone: the API checks the role only (BR-FARM-08). The customer profile is part of each save and is read before the debt, so
/// of two saves that read one debt only the first succeeds and the other answers MSG06: the debt told to the Owner and in MSG-DEBT-04/06 rests on a true reading (as the
/// supplier payments). Confirmations and cancellations: <see cref="CustomerPaymentStatusService"/>.
/// </summary>
public sealed class CustomerPaymentService(
    ICustomerPaymentRepository payments,
    ICustomerRepository customers,
    ICashAccountRepository cashAccounts,
    CashAccountService bankAccounts,
    CustomerDebtReader debts,
    CustomerPaymentNotifications notifications,
    CustomerPaymentViews views,
    CustomerPaymentCodeService codes,
    IAccountRepository accounts,
    ICurrentUser currentUser,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public const string PendingTransferCreatedAction = "CustomerPayment.PendingTransferCreated";

    /// <summary>EMVCo: a transaction amount holds at most 13 characters.</summary>
    private const decimal MaxQrAmount = 9_999_999_999_999m;

    /// <summary>DEBT-02 step 1 / DEBT-02.3 step 2 for any profile; 404 for an unknown one.</summary>
    public async Task<Result<CustomerPaymentForm>> FormAsync(Guid customerId, CancellationToken cancellationToken)
    {
        if (await customers.GetForEditAsync(customerId, cancellationToken) is not { } customer)
        {
            return AppError.NotFound;
        }

        var allocation = await debts.AllocationAsync(customerId, cancellationToken);
        var collectors = await CollectorsAsync(cancellationToken);
        var banks = (await bankAccounts.ListAsync(CashAccountKind.Bank, includeInactive: false, cancellationToken))
            .Where(a => a.BankBin is not null && ReceivingBankAccountRules.IsValidAccountNumber(a.AccountNumber))
            .Select(a => new CustomerPaymentBankAccount(a.Id, a.BankBin!, a.ProviderName, a.AccountNumber, a.HolderName, a.IsVietQr))
            .ToList();
        return new CustomerPaymentForm(
            new CustomerDebtCustomer(customer.Id, customer.Name, customer.PhoneNumber, customer.Code), allocation.Debt, CustomerDebtMath.OwedBack(allocation.DebtApartFromReserved),
            allocation.DebtAgeDays, allocation.UnpaidInvoices, collectors, banks, allocation.ReservedAdvances);
    }

    public async Task<Result<CustomerPaymentDetails>> GetAsync(Guid paymentId, CancellationToken cancellationToken) =>
        await payments.GetAsync(paymentId, cancellationToken) is { } payment ? await views.ViewAsync(payment, cancellationToken) : AppError.NotFound;

    /// <summary>The VietQR code of a payment that waits for its transfer, again (reprint); 409 MSG-GEN-09 for any other payment.</summary>
    public async Task<Result<CustomerPaymentQr>> GetQrAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        if (await payments.GetAsync(paymentId, cancellationToken) is not { } payment)
        {
            return AppError.NotFound;
        }

        if (payment.StatusId != StatusIds.CustomerPayment.PendingTransfer)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Customer payment {payment.Code}", statusCatalog.DisplayNameOf(payment.StatusId)));
        }

        return QrOf(payment) is { } qr ? qr : AppError.Invalid("Cash account");
    }

    /// <summary>
    /// DEBT-02 steps 2-7 (<paramref name="kind"/> Payment) or DEBT-02.3 (PaidBack). One question at a time, answered by sending its flag with the same request:
    /// MSG-DEBT-04 (payments only), then MSG-DEBT-05. Money paid back above the money owed back is refused (MSG-DEBT-06).
    /// </summary>
    public Task<Result<CustomerPaymentSaved>> RecordAsync(RecordCustomerPaymentRequest request, CustomerPaymentKind kind, CancellationToken cancellationToken) =>
        SaveAsync(request, kind, CustomerPaymentMode.Direct, cancellationToken);

    /// <summary>
    /// A voucher of the cash book with "Tính vào công nợ" (the cash book calls it): a receipt only for a customer who owes (and at most what is owed), money paid back only for a
    /// customer the cooperative owes (and at most what is owed back). It is saved as a Completed customer payment, with no bank reference; <c>Allocations</c> null sets the unpaid
    /// invoices oldest first up to the amount, an empty list only takes it off the debt.
    /// </summary>
    public Task<Result<CustomerPaymentSaved>> RecordFromCashVoucherAsync(RecordCustomerPaymentRequest request, CustomerPaymentKind kind, CancellationToken cancellationToken) =>
        SaveAsync(request, kind, CustomerPaymentMode.FromCashVoucher, cancellationToken);

    /// <summary>
    /// "Tạo QR" and "Tạo phiếu thu &amp; In QR": a payment of kind Payment that waits for the customer's transfer, with the VietQR code that pays its code into the bank account chosen.
    /// It counts in no debt and no fund until the transfer arrives. Without allocations, the unpaid invoices are set oldest first up to the amount and the discount.
    /// </summary>
    public Task<Result<CustomerPaymentSaved>> CreatePendingTransferAsync(RecordCustomerPaymentRequest request, CancellationToken cancellationToken) =>
        SaveAsync(request, CustomerPaymentKind.Payment, CustomerPaymentMode.WaitsForTransfer, cancellationToken);

    /// <summary>
    /// An advance on an order for money that has arrived, saved at once: cash waits for the Accountant to confirm it (Awaiting confirmation), a transfer (with its bank reference) is Completed. The money is reserved
    /// for the order while it is open (<see cref="CustomerDebtAllocator"/>). The customer of the order is the payer; an advance carries no discount and names no invoice.
    /// </summary>
    public Task<Result<CustomerPaymentSaved>> RecordOrderAdvanceAsync(SalesOrder order, RecordCustomerPaymentRequest request, CancellationToken cancellationToken) =>
        SaveAsync(request with { CustomerId = order.CustomerId, Allocations = null, DiscountAmount = 0m }, CustomerPaymentKind.Payment, CustomerPaymentMode.OrderAdvance, cancellationToken, order);

    /// <summary>The same advance as a VietQR code that waits for the transfer of the customer (it counts nowhere until the transfer arrives).</summary>
    public Task<Result<CustomerPaymentSaved>> CreateOrderAdvanceTransferAsync(SalesOrder order, RecordCustomerPaymentRequest request, CancellationToken cancellationToken) =>
        SaveAsync(request with { CustomerId = order.CustomerId, Allocations = null, DiscountAmount = 0m }, CustomerPaymentKind.Payment, CustomerPaymentMode.WaitsForTransfer, cancellationToken, order);

    /// <summary>
    /// The advance of an order that is being saved: checked and added to the unit of work of the caller with its audit row, but not saved, so the order and its advance are one save. The caller reads the payment again
    /// after the save (<see cref="ICustomerPaymentRepository.GetAsync"/>) for its view and, for a payment that waits for its transfer, its VietQR code (<see cref="QrOf"/>).
    /// </summary>
    public async Task<Result<CustomerPayment>> StageOrderAdvanceAsync(SalesOrder order, RecordCustomerPaymentRequest request, bool waitsForTransfer, CancellationToken cancellationToken)
    {
        var staged = await StageAsync(
            request with { CustomerId = order.CustomerId, Allocations = null, DiscountAmount = 0m, DeduplicationKey = null }, CustomerPaymentKind.Payment,
            waitsForTransfer ? CustomerPaymentMode.WaitsForTransfer : CustomerPaymentMode.OrderAdvance, order, cancellationToken);
        return staged.IsSuccess ? staged.Value!.Payment : staged.Error!;
    }

    private async Task<Result<CustomerPaymentSaved>> SaveAsync(
        RecordCustomerPaymentRequest request, CustomerPaymentKind kind, CustomerPaymentMode mode, CancellationToken cancellationToken, SalesOrder? order = null)
    {
        var key = CustomerPaymentRules.Trimmed(request.DeduplicationKey);
        if (key is not null && await FirstOfKeyAsync(key, cancellationToken) is { } first)
        {
            return first;
        }

        var staged = await StageAsync(request, kind, mode, order, cancellationToken);
        if (!staged.IsSuccess)
        {
            return staged.Error!;
        }

        var (payment, debtAfter, waitsForTransfer) = staged.Value!;
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

        var written = await payments.GetAsync(payment.Id, cancellationToken) ?? payment;
        return await views.SavedAsync(written, debtAfter, cancelled: false, cancellationToken, waitsForTransfer ? QrOf(written) : null);
    }

    private sealed record StagedPayment(CustomerPayment Payment, decimal DebtAfter, bool WaitsForTransfer);

    /// <summary>Everything of a save but the save itself: the checks, the payment, its audit row and the notice to the Owner, all in the unit of work of the caller.</summary>
    private async Task<Result<StagedPayment>> StageAsync(
        RecordCustomerPaymentRequest request, CustomerPaymentKind kind, CustomerPaymentMode mode, SalesOrder? order, CancellationToken cancellationToken)
    {
        var waitsForTransfer = mode == CustomerPaymentMode.WaitsForTransfer;
        var key = CustomerPaymentRules.Trimmed(request.DeduplicationKey);
        var now = timeProvider.GetUtcNow();
        var invalid = CustomerPaymentRules.InvalidFields(request, now, kind, mode, forOrder: order is not null);
        var customer = request.CustomerId is { } customerId ? await customers.GetForEditAsync(customerId, cancellationToken) : null;
        if (customer is null)
        {
            invalid.Insert(0, "Customer");
        }

        // The code of a VietQR code is its transfer content, which holds 25 characters.
        if (waitsForTransfer && DocumentCodes.Typed(request.Code)?.Length > VietQrPayload.MaxContentLength)
        {
            invalid.Add("Code");
        }

        var collectorId = request.CollectedByAccountId ?? currentUser.AccountId!.Value;
        if (request.CollectedByAccountId is { } named && !(await CollectorsAsync(cancellationToken)).Any(a => a.Id == named))
        {
            invalid.Add("Collected by");
        }

        if (waitsForTransfer && request.Amount > MaxQrAmount)
        {
            invalid.Add("Amount");
        }

        var movesMoney = request.Amount is > 0;
        var cashAccount = await CashAccountAsync(request, movesMoney, waitsForTransfer, invalid, cancellationToken);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid.Distinct().ToList());
        }

        if (customer!.MergedIntoCustomerId is not null)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"{CustomerService.ObjectName} {customer.Name}", CustomerService.MergedStatus));
        }

        var amount = request.Amount!.Value;
        var discount = kind == CustomerPaymentKind.Payment ? request.DiscountAmount : 0m;
        var reading = await debts.AllocationAsync(customer.Id, cancellationToken);
        var debt = reading.Debt;
        // What the customer owes on invoices, or the credit that is really free: the money reserved for open orders is neither owed back nor a debt to pay down, so the limits below read the debt apart from it.
        var limitDebt = reading.DebtApartFromReserved;
        var refused = order is not null
            ? null
            : kind == CustomerPaymentKind.PaidBack
                ? CustomerPaymentRules.PaidBackAboveOwedBack(customer.Name, limitDebt, amount)
                : mode == CustomerPaymentMode.FromCashVoucher
                    ? CountInDebtRefusal(limitDebt, amount)
                    : CustomerPaymentRules.AboveDebtQuestion(customer.Name, limitDebt, amount + discount, request.ConfirmAboveDebt);
        if (refused is not null)
        {
            return refused;
        }

        // An advance on an order names no invoice: its money is held for the invoices made from that order.
        IReadOnlyList<(Guid InvoiceId, decimal Amount)> parts = [];
        if (order is null)
        {
            parts = AllocationParts(request, kind, amount + discount, reading, waitsForTransfer || (mode == CustomerPaymentMode.FromCashVoucher && request.Allocations is null), out var partError);
            if (partError is not null)
            {
                return partError;
            }
        }

        var method = waitsForTransfer ? CustomerPaymentMethod.OtherTransfer : movesMoney ? request.Method!.Value : CustomerPaymentMethod.Cash;
        var bankReference = !waitsForTransfer && movesMoney && method == CustomerPaymentMethod.OtherTransfer ? CustomerPaymentRules.Trimmed(request.BankReference) : null;
        if (bankReference is not null && !request.ConfirmDuplicateBankReference
            && await payments.FindNotCancelledByBankReferenceAsync(bankReference, cancellationToken) is { } existing)
        {
            return CustomerPaymentRules.DuplicateBankReferenceQuestion(existing);
        }

        // The typed code, or one taken once the payment is valid and confirmed, so a refused request spends no number; never given twice.
        var (code, duplicate) = await codes.ForNewAsync(request.Code, cancellationToken);
        if (duplicate is not null)
        {
            return duplicate;
        }

        var me = currentUser.AccountId!.Value;
        var paidAt = waitsForTransfer ? now.ToOffset(BusinessClock.UtcOffset) : CustomerPaymentRules.PaidAt(request, now);
        var payment = new CustomerPayment
        {
            Code = code,
            CustomerId = customer.Id,
            Customer = customer,
            Kind = kind,
            PaymentDate = BusinessClock.ToBusinessDate(paidAt),
            PaidAt = paidAt,
            Amount = amount,
            DiscountAmount = discount,
            Method = method,
            BankReference = bankReference,
            CashAccountId = method is CustomerPaymentMethod.OtherTransfer or CustomerPaymentMethod.EWallet ? cashAccount?.Id : null,
            CollectedByAccountId = waitsForTransfer ? null : collectorId,
            RecordedByAccountId = me,
            Note = CustomerPaymentRules.Trimmed(request.Note),
            DeduplicationKey = key,
            SalesOrderId = order?.Id,
            SalesOrder = order,
        };
        if (waitsForTransfer)
        {
            payment.StatusId = StatusIds.CustomerPayment.PendingTransfer;
        }
        else if (order is not null && method == CustomerPaymentMethod.Cash)
        {
            // Cash taken for an order is Awaiting confirmation until the Accountant confirms it, like the cash taken at the port with an invoice.
            payment.StatusId = StatusIds.CustomerPayment.AwaitingConfirmation;
        }
        else
        {
            // BR-DEBT-03: the Accountant records only money that has reached the cooperative (or left it), so it is Completed at once.
            payment.StatusId = StatusIds.CustomerPayment.Completed;
            payment.ConfirmedByAccountId = me;
            payment.ConfirmedAt = now;
        }

        foreach (var (invoiceId, partAmount) in parts)
        {
            payment.Allocations.Add(new CustomerPaymentAllocation { PaymentId = payment.Id, InvoiceId = invoiceId, Amount = partAmount });
        }

        payments.Add(payment);
        unitOfWork.MarkChanged(customer);
        audit.Record(new AuditEntry(waitsForTransfer ? PendingTransferCreatedAction : SalesInvoiceWriter.PaymentRecordedAction, SalesInvoiceWriter.PaymentRecordType)
        {
            RecordId = payment.Id,
            RecordCode = payment.Code,
            After = new
            {
                payment.Kind, payment.CustomerId, payment.Amount, payment.DiscountAmount, payment.PaymentDate, payment.PaidAt, payment.Method, payment.BankReference,
                payment.CashAccountId, payment.CollectedByAccountId, payment.Note, Status = statusCatalog.CodeOf(payment.StatusId),
                Allocations = parts.Select(p => new { p.InvoiceId, p.Amount }).ToList(),
                SalesOrderCode = order?.Code,
            },
        });
        var debtAfter = waitsForTransfer ? debt : CustomerPaymentRules.DebtAfter(debt, kind, amount, cancelled: false, discount);
        if (!waitsForTransfer && payment.StatusId == StatusIds.CustomerPayment.Completed)
        {
            await notifications.TransactionAsync(payment, cancelled: false, debtAfter, cancellationToken);
        }

        return new StagedPayment(payment, debtAfter, waitsForTransfer);
    }

    /// <summary>A receipt that counts in the debt needs a customer who owes, and at most what is owed.</summary>
    private static AppError? CountInDebtRefusal(decimal debt, decimal amount) =>
        debt <= 0
            ? AppError.Invalid("Count in debt", "The customer owes nothing.")
            : amount > debt ? AppError.Invalid("Amount", $"The customer owes {debt:0} VND.") : null;

    /// <summary>Who may be named as having collected the money or paid it back: the Active Owner and staff accounts, by name.</summary>
    private async Task<IReadOnlyList<AccountRef>> CollectorsAsync(CancellationToken cancellationToken) =>
        (await accounts.ListActiveInternalAsync(cancellationToken)).Select(a => new AccountRef(a.Id, a.DisplayName)).OrderBy(a => a.Name, StringComparer.Ordinal).ToList();

    /// <summary>
    /// The bank account (an e-wallet account for an e-wallet voucher) named, which only a transfer may name (a payment with no money names none) and which must be an active Bank account of the cash book; for a VietQR code
    /// it also needs the bank and a number the code can hold. "Cash account" is added to <paramref name="invalid"/> otherwise.
    /// </summary>
    private async Task<CashAccount?> CashAccountAsync(
        RecordCustomerPaymentRequest request, bool movesMoney, bool waitsForTransfer, List<string> invalid, CancellationToken cancellationToken)
    {
        if (request.CashAccountId is not { } id)
        {
            return null;
        }

        var wallet = movesMoney && !waitsForTransfer && request.Method == CustomerPaymentMethod.EWallet;
        var transfer = waitsForTransfer || (movesMoney && request.Method == CustomerPaymentMethod.OtherTransfer) || wallet;
        var account = await cashAccounts.GetForEditAsync(id, cancellationToken);
        if (!transfer || account is null || account.Kind != (wallet ? CashAccountKind.EWallet : CashAccountKind.Bank) || !account.IsActive
            || (waitsForTransfer && (account.BankBin is null || !ReceivingBankAccountRules.IsValidAccountNumber(account.AccountNumber))))
        {
            invalid.Add("Cash account");
        }

        return account;
    }

    /// <summary>
    /// The parts of the payment: the ones sent (each an invoice of the customer with that much left to pay), or for a payment that waits for a transfer the unpaid invoices oldest
    /// first up to what the payment covers; none for any other payment that names none.
    /// </summary>
    private static IReadOnlyList<(Guid InvoiceId, decimal Amount)> AllocationParts(
        RecordCustomerPaymentRequest request, CustomerPaymentKind kind, decimal covers, CustomerDebtAllocation reading, bool oldestFirstWhenNone, out AppError? error)
    {
        error = null;
        if (kind != CustomerPaymentKind.Payment)
        {
            return [];
        }

        var unpaid = reading.UnpaidInvoices.ToDictionary(u => u.Id);
        if (request.Allocations is { } sent)
        {
            var parts = new List<(Guid InvoiceId, decimal Amount)>();
            for (var i = 0; i < sent.Count; i++)
            {
                var field = $"Allocation {i + 1}";
                if (!unpaid.TryGetValue(sent[i].InvoiceId!.Value, out var invoice))
                {
                    error = AppError.Invalid(field, "Not an invoice of this customer with money left to pay.");
                    return [];
                }

                if (sent[i].Amount > invoice.Unpaid)
                {
                    error = AppError.Invalid(field, $"Invoice {invoice.Code} has {invoice.Unpaid:0} VND left to pay.");
                    return [];
                }

                parts.Add((invoice.Id, sent[i].Amount!.Value));
            }

            return parts;
        }

        if (!oldestFirstWhenNone)
        {
            return [];
        }

        var oldestFirst = new List<(Guid InvoiceId, decimal Amount)>();
        var remaining = covers;
        foreach (var invoice in reading.UnpaidInvoices)
        {
            if (remaining <= 0)
            {
                break;
            }

            var part = Math.Min(remaining, invoice.Unpaid);
            oldestFirst.Add((invoice.Id, part));
            remaining -= part;
        }

        return oldestFirst;
    }

    /// <summary>The VietQR code of a payment waiting for its transfer: its code as the content, its amount, the bank account chosen. Null when the account cannot make one.</summary>
    internal static CustomerPaymentQr? QrOf(CustomerPayment payment)
    {
        if (payment.CashAccount is not { BankBin: { } bin } account || !ReceivingBankAccountRules.IsValidAccountNumber(account.AccountNumber) || payment.Amount <= 0)
        {
            return null;
        }

        var content = VietQrPayload.ContentOf(payment.Code);
        return new CustomerPaymentQr(
            VietQrPayload.Build(bin, account.AccountNumber, (long)payment.Amount, content), bin, account.ProviderName, account.AccountNumber, account.HolderName, payment.Amount, content);
    }

    private async Task<CustomerPaymentSaved?> FirstOfKeyAsync(string key, CancellationToken cancellationToken)
    {
        if (await payments.FindByDeduplicationKeyAsync(key, cancellationToken) is not { } first)
        {
            return null;
        }

        decimal? debt = first.CustomerId is { } customerId ? await debts.DebtAsync(customerId, cancellationToken) : null;
        return await views.SavedAsync(first, debt, cancelled: false, cancellationToken, first.StatusId == StatusIds.CustomerPayment.PendingTransfer ? QrOf(first) : null);
    }
}
