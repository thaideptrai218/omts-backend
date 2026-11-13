using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Purchasing.Payments;
using OMTS.Application.Features.Sales.Customers;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Payments;

/// <summary>
/// A correction of the debt of a customer by hand ("Điều chỉnh"): a signed amount with a reason and a time; above zero the customer owes more, below zero less. It is for the
/// Owner and the Accountant (CUSTOMER_PAYMENTS Edit) and appears in the statement. It never changes: one made by mistake is cancelled with a reason and then counts 0.
/// The customer profile is part of each save, so of two saves that read one debt only the first succeeds (MSG06).
/// </summary>
public sealed class CustomerDebtAdjustmentService(
    ICustomerDebtAdjustmentRepository adjustments,
    ICustomerRepository customers,
    CustomerDebtReader debts,
    IAccountRepository accounts,
    IDocumentNumberAllocator numbers,
    ICurrentUser currentUser,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const string RecordType = nameof(CustomerDebtAdjustment);

    public async Task<Result<CustomerDebtAdjustmentSaved>> RecordAsync(RecordCustomerDebtAdjustmentRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var invalid = new List<string>();
        var customer = request.CustomerId is { } customerId ? await customers.GetForEditAsync(customerId, cancellationToken) : null;
        if (customer is null)
        {
            invalid.Add("Customer");
        }

        // Whole VND, never zero: the column has no decimals, and a zero adjustment changes nothing.
        if (request.Amount is not { } amount || amount == 0 || Math.Abs(amount) > SupplierPaymentRules.MaxAmount || amount != decimal.Truncate(amount))
        {
            invalid.Add("Amount");
        }

        var reason = CustomerPaymentRules.Trimmed(request.Reason);
        if (reason is null || reason.Length > SupplierPaymentRules.MaxNoteLength)
        {
            invalid.Add("Reason");
        }

        if (request.OccurredAt is { } occurredAt && occurredAt > now + CustomerPaymentRules.ClockTolerance)
        {
            invalid.Add("Occurred at");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (customer!.MergedIntoCustomerId is not null)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"{CustomerService.ObjectName} {customer.Name}", CustomerService.MergedStatus));
        }

        // Read before the save so that a parallel payment or adjustment (which marks the profile too) conflicts with this one.
        var debt = await debts.DebtAsync(customer.Id, cancellationToken);
        var at = (request.OccurredAt ?? now).ToOffset(BusinessClock.UtcOffset);
        var adjustment = new CustomerDebtAdjustment
        {
            Code = SalesDocumentCodes.AdjustmentCode(await numbers.NextAsync(DocumentSequenceKeys.CustomerDebtAdjustment, cancellationToken)),
            CustomerId = customer.Id,
            Customer = customer,
            Amount = request.Amount!.Value,
            OccurredAt = at,
            AdjustmentDate = BusinessClock.ToBusinessDate(at),
            Reason = reason!,
            RecordedByAccountId = currentUser.AccountId!.Value,
        };
        adjustments.Add(adjustment);
        unitOfWork.MarkChanged(customer);
        audit.Record(new AuditEntry("CustomerDebtAdjustment.Recorded", RecordType)
        {
            RecordId = adjustment.Id,
            RecordCode = adjustment.Code,
            After = new { adjustment.CustomerId, adjustment.Amount, adjustment.OccurredAt, adjustment.Reason },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await SavedAsync(adjustment, debt + adjustment.Amount, $"Adjustment {adjustment.Code}", cancellationToken);
    }

    /// <summary>Recorded → Cancelled with a reason: it no longer counts, also in past periods.</summary>
    public async Task<Result<CustomerDebtAdjustmentSaved>> CancelAsync(Guid adjustmentId, CancelCustomerPaymentRequest request, CancellationToken cancellationToken)
    {
        var adjustment = await adjustments.GetForEditAsync(adjustmentId, cancellationToken);
        if (adjustment is null)
        {
            return AppError.NotFound;
        }

        if (adjustment.CancelledAt is not null)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Adjustment {adjustment.Code}", "Cancelled"));
        }

        if (CustomerPaymentRules.Reason(request.Reason) is not { } reason)
        {
            return AppError.Invalid("Reason");
        }

        if (request.RowVersion is not null && await rowVersions.CheckAsync(adjustment, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var debt = await debts.DebtAsync(adjustment.CustomerId, cancellationToken);
        adjustment.CancelledAt = timeProvider.GetUtcNow();
        adjustment.CancelledByAccountId = currentUser.AccountId;
        adjustment.CancelReason = reason;
        unitOfWork.MarkChanged(adjustment.Customer);
        audit.Record(new AuditEntry("CustomerDebtAdjustment.Cancelled", RecordType)
        {
            RecordId = adjustment.Id,
            RecordCode = adjustment.Code,
            Before = new { Status = "Recorded" },
            After = new { Status = "Cancelled", adjustment.Amount },
            Reason = reason,
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await SavedAsync(adjustment, debt - adjustment.Amount, $"Cancellation of adjustment {adjustment.Code}", cancellationToken);
    }

    private async Task<CustomerDebtAdjustmentSaved> SavedAsync(CustomerDebtAdjustment adjustment, decimal debtAfter, string what, CancellationToken cancellationToken)
    {
        var message = UserMessages.Saved(what);
        return new CustomerDebtAdjustmentSaved(await ViewAsync(adjustment, cancellationToken), debtAfter, message.Text, message.Code);
    }

    private async Task<CustomerDebtAdjustmentView> ViewAsync(CustomerDebtAdjustment a, CancellationToken cancellationToken)
    {
        var names = await accounts.GetDisplayNamesAsync([a.RecordedByAccountId], cancellationToken);
        return new CustomerDebtAdjustmentView(
            a.Id, a.Code, new CustomerDebtCustomer(a.Customer.Id, a.Customer.Name, a.Customer.PhoneNumber, a.Customer.Code), a.Amount, a.OccurredAt, a.AdjustmentDate, a.Reason,
            new AccountRef(a.RecordedByAccountId, names.GetValueOrDefault(a.RecordedByAccountId) ?? string.Empty), a.CancelledAt is not null, a.CancelledAt, a.CancelReason,
            RowVersionGuard.Encode(a.RowVersion));
    }
}
