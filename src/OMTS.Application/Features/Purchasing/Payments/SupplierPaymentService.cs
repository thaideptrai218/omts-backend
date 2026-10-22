using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Payments;

public sealed record RecordSupplierPaymentRequest(
    Guid? SupplierId, Guid? ReceiptId, decimal? Amount, DateOnly? PaymentDate, PaymentMethod? Method, string? ReferenceCode, string? DeduplicationKey);

public sealed record AllocatePaymentRequest(Guid? ReceiptId);

public sealed record SupplierPaymentView(
    Guid Id, SupplierRef Supplier, Guid? ReceiptId, string? ReceiptCode, decimal Amount, DateOnly PaymentDate, PaymentMethod Method,
    string? ReferenceCode, int StatusId, string Status, string? CancelReason);

public sealed record SupplierPaymentPage(Paged<SupplierPaymentView> Page, decimal TotalAmount);

/// <summary>
/// INP-06 – the Accountant records money paid to suppliers against Completed receipts (All zones grant,
/// checked by the API filter), allocates detached payments and reads payment history. Payments are never
/// more than the receipt outstanding; a repeated deduplication key returns the first payment.
/// </summary>
public sealed class SupplierPaymentService(
    ISupplierPaymentRepository payments,
    IPurchaseReceiptRepository receipts,
    ISupplierRepository suppliers,
    ICurrentUser currentUser,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<Result<SupplierPaymentView>> RecordAsync(RecordSupplierPaymentRequest request, CancellationToken cancellationToken)
    {
        var key = string.IsNullOrWhiteSpace(request.DeduplicationKey) ? null : request.DeduplicationKey.Trim();
        if (key is not null && await payments.FindByDeduplicationKeyAsync(key, cancellationToken) is { } first)
        {
            return await ViewAsync(first, cancellationToken);
        }

        var invalid = new List<string>();
        if (request.SupplierId is null || await suppliers.GetForEditAsync(request.SupplierId.Value, cancellationToken) is null)
        {
            invalid.Add("Supplier");
        }

        var receipt = request.ReceiptId is { } receiptId ? await receipts.GetForEditAsync(receiptId, cancellationToken) : null;
        if (receipt is null || receipt.SupplierId != request.SupplierId)
        {
            invalid.Add("Receipt");
        }

        if (request.Amount is not > 0)
        {
            invalid.Add("Amount");
        }

        if (request.PaymentDate is not { } paymentDate || paymentDate > timeProvider.Today())
        {
            invalid.Add("Payment date");
        }

        if (request.Method is not { } method || !Enum.IsDefined(method))
        {
            invalid.Add("Method");
        }

        if (request.ReferenceCode?.Trim().Length > 100 || key?.Length > 100)
        {
            invalid.Add(key?.Length > 100 ? "Deduplication key" : "Reference code");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (AmountError(receipt!, request.Amount!.Value) is { } amountError)
        {
            return amountError;
        }

        var payment = new SupplierPayment
        {
            SupplierId = receipt!.SupplierId,
            ReceiptId = receipt.Id,
            Amount = request.Amount!.Value,
            PaymentDate = request.PaymentDate!.Value,
            Method = request.Method!.Value,
            ReferenceCode = string.IsNullOrWhiteSpace(request.ReferenceCode) ? null : request.ReferenceCode.Trim(),
            DeduplicationKey = key,
            RecordedByAccountId = currentUser.AccountId!.Value,
        };
        receipt.Payments.Add(payment);
        payments.Add(payment);
        // Parallel payments on one receipt cannot both pass the outstanding check: the receipt row is saved too.
        unitOfWork.MarkChanged(receipt);
        audit.Record(new AuditEntry("SupplierPayment.Recorded", nameof(SupplierPayment))
        {
            RecordId = payment.Id, RecordCode = receipt.Code, ZoneId = receipt.ZoneId,
            After = new { payment.Amount, payment.Method, payment.PaymentDate, payment.ReferenceCode, ReceiptId = receipt.Id },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ViewAsync(payment, cancellationToken);
    }

    /// <summary>Allocates a payment detached by a receipt cancellation to a Completed receipt of the same supplier.</summary>
    public async Task<Result<SupplierPaymentView>> AllocateAsync(Guid paymentId, AllocatePaymentRequest request, CancellationToken cancellationToken)
    {
        var payment = await payments.GetForEditAsync(paymentId, cancellationToken);
        if (payment is null)
        {
            return AppError.NotFound;
        }

        if (payment.ReceiptId is not null || payment.StatusId != StatusIds.SupplierPayment.Recorded)
        {
            return AppError.Conflict(UserMessages.CannotChangeStatus(
                "Supplier payment", statusCatalog.DisplayNameOf(payment.StatusId), "Allocated", "only an unallocated Recorded payment can be allocated"));
        }

        var receipt = request.ReceiptId is { } receiptId ? await receipts.GetForEditAsync(receiptId, cancellationToken) : null;
        if (receipt is null || receipt.SupplierId != payment.SupplierId)
        {
            return AppError.Invalid("Receipt");
        }

        if (AmountError(receipt, payment.Amount) is { } amountError)
        {
            return amountError;
        }

        payment.ReceiptId = receipt.Id;
        receipt.Payments.Add(payment);
        unitOfWork.MarkChanged(receipt);
        audit.Record(new AuditEntry("SupplierPayment.Allocated", nameof(SupplierPayment))
        {
            RecordId = payment.Id, RecordCode = receipt.Code, ZoneId = receipt.ZoneId, Before = new { ReceiptId = (Guid?)null }, After = new { ReceiptId = receipt.Id },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ViewAsync(payment, cancellationToken);
    }

    public async Task<SupplierPaymentPage> SearchAsync(
        Guid? supplierId, Guid? receiptId, Guid? zoneId, PaymentMethod? method, int? statusId, DateOnly? from, DateOnly? to,
        int page, int pageSize, CancellationToken cancellationToken)
    {
        (page, pageSize) = (Math.Max(page, 1), Math.Clamp(pageSize, 1, 100));
        var (items, total, amount) = await payments.SearchAsync(supplierId, receiptId, zoneId, method, statusId, from, to, page, pageSize, cancellationToken);
        return new SupplierPaymentPage(new Paged<SupplierPaymentView>(items.Select(View).ToList(), page, pageSize, total), amount);
    }

    /// <summary>Completed receipt of the supplier, multiple of 1,000 and at most its outstanding.</summary>
    private AppError? AmountError(PurchaseReceipt receipt, decimal amount)
    {
        if (receipt.StatusId != StatusIds.PurchaseReceipt.Completed)
        {
            return AppError.Conflict(UserMessages.CannotChangeStatus(
                "Purchase receipt", statusCatalog.DisplayNameOf(receipt.StatusId), "Paid", "only a Completed receipt can be paid"));
        }

        if (!PurchaseMoney.IsThousands(amount))
        {
            return new AppError(ErrorKind.Validation, UserMessages.NotThousands, new Dictionary<string, object?> { ["field"] = "Amount" });
        }

        var outstanding = PurchaseMoney.Of(receipt).Outstanding;
        return amount > outstanding ? PurchaseReceiptValidator.ExceedsLimit("Amount", outstanding) : null;
    }

    private async Task<SupplierPaymentView> ViewAsync(SupplierPayment payment, CancellationToken cancellationToken)
    {
        payment.Supplier ??= (await suppliers.GetForEditAsync(payment.SupplierId, cancellationToken))!;
        if (payment.ReceiptId is { } receiptId && payment.Receipt is null)
        {
            payment.Receipt = await receipts.GetForEditAsync(receiptId, cancellationToken);
        }

        return View(payment);
    }

    private SupplierPaymentView View(SupplierPayment p) => new(
        p.Id, new SupplierRef(p.Supplier.Id, p.Supplier.Name, p.Supplier.PhoneNumber), p.ReceiptId, p.Receipt?.Code, p.Amount, p.PaymentDate,
        p.Method, p.ReferenceCode, p.StatusId, statusCatalog.DisplayNameOf(p.StatusId), p.CancelReason);
}
