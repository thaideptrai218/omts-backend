using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Sales.Payments;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Orders;

/// <summary>
/// An advance added to an order that already exists: a customer payment that names the order (<see cref="CustomerPaymentService.RecordOrderAdvanceAsync"/>), at most what the order is still short of; a VietQR code
/// waits for the transfer of the customer. A buyer with no profile pays no advance. The Owner, the Zone Managers (SALES_ORDERS Edit) and the Accountant (CUSTOMER_PAYMENTS Create) may take one.
/// </summary>
public sealed class SalesOrderAdvanceService(
    ISalesOrderRepository orders,
    ICustomerPaymentRepository paymentRows,
    SalesOrderAccess access,
    SalesOrderViews views,
    CustomerPaymentService payments,
    IStatusCatalog statusCatalog,
    TimeProvider timeProvider)
{
    public async Task<Result<SalesOrderAdvanceSaved>> AddAsync(Guid orderId, SalesOrderAdvanceInput input, CancellationToken cancellationToken)
    {
        var order = await orders.GetForEditAsync(orderId, cancellationToken);
        if (order is null || !await access.MayAsync(PermissionAction.View, cancellationToken))
        {
            return AppError.NotFound;
        }

        if (!await access.MayTakeAdvanceAsync(cancellationToken))
        {
            return AppError.Forbidden;
        }

        var key = string.IsNullOrWhiteSpace(input.DeduplicationKey) ? null : input.DeduplicationKey.Trim();
        if (key?.Length > SalesOrderRules.MaxKeyLength)
        {
            return AppError.Invalid("Deduplication key");
        }

        // A repeated key returns the advance it made.
        if (key is not null && await paymentRows.FindByDeduplicationKeyAsync(key, cancellationToken) is { } first && first.SalesOrderId == order.Id)
        {
            return await SavedAsync(order, first, cancellationToken);
        }

        if (!SalesOrderRules.IsOpen(order.StatusId))
        {
            return AppError.Conflict(UserMessages.WrongStatus($"{SalesOrderCodeService.ObjectName} {order.Code}", statusCatalog.DisplayNameOf(order.StatusId)));
        }

        var invalid = new List<string>();
        var entries = SalesOrderEntryReader.ReadAdvance(input, walkIn: order.CustomerId is null, invalid);
        if (entries is null)
        {
            return AppError.InvalidFields(invalid.Count > 0 ? invalid : ["Advance"]);
        }

        // The total of the advances never goes above the total of the order: the ones waiting for their transfer count, the cancelled ones do not.
        var existing = (await orders.AdvancesOfAsync(order.Id, cancellationToken))
            .Where(p => p.Kind == CustomerPaymentKind.Payment && p.StatusId != StatusIds.CustomerPayment.Cancelled).Sum(p => p.Amount);
        var room = order.Total - existing;
        if (entries.Amount > room)
        {
            return AppError.Invalid("Advance amount", $"The order is short of {Math.Max(0m, room):0} VND: the advance cannot be more.");
        }

        var request = new RecordCustomerPaymentRequest(
            order.CustomerId,
            PaymentDate: null,
            entries.Amount,
            entries.Method == SalesOrderAdvanceMethod.Cash ? CustomerPaymentMethod.Cash : CustomerPaymentMethod.OtherTransfer,
            entries.BankReference,
            entries.CollectedById,
            entries.Note,
            ConfirmDuplicateBankReference: entries.ConfirmDuplicateBankReference,
            DeduplicationKey: key,
            PaidAt: entries.PaidAt ?? timeProvider.GetUtcNow(),
            CashAccountId: entries.CashAccountId);
        var saved = entries.Method == SalesOrderAdvanceMethod.VietQr
            ? await payments.CreateOrderAdvanceTransferAsync(order, request, cancellationToken)
            : await payments.RecordOrderAdvanceAsync(order, request, cancellationToken);
        if (!saved.IsSuccess)
        {
            return saved.Error!;
        }

        var view = await views.ViewAsync(order, cancellationToken);
        return new SalesOrderAdvanceSaved(view, view.Advances.First(a => a.PaymentId == saved.Value!.Payment.Id), saved.Value!.Qr);
    }

    private async Task<Result<SalesOrderAdvanceSaved>> SavedAsync(SalesOrder order, CustomerPayment payment, CancellationToken cancellationToken)
    {
        var view = await views.ViewAsync(order, cancellationToken);
        var qr = payment.StatusId == StatusIds.CustomerPayment.PendingTransfer && await paymentRows.GetAsync(payment.Id, cancellationToken) is { } written
            ? CustomerPaymentService.QrOf(written)
            : null;
        return new SalesOrderAdvanceSaved(view, view.Advances.First(a => a.PaymentId == payment.Id), qr);
    }
}
