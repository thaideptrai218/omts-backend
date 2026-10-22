using System.Globalization;
using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts;

public sealed record ReturnLineInput(Guid? ReceiptLineId, decimal? Quantity);

public sealed record CreatePurchaseReturnRequest(
    DateOnly? ReturnDate,
    string? Reason,
    decimal? RefundAmount,
    PaymentMethod? RefundMethod,
    decimal? DebtReductionAmount,
    IReadOnlyList<ReturnLineInput>? Lines);

public sealed record PurchaseReturnLineView(Guid ReceiptLineId, string ItemCode, string ItemName, string Unit, decimal Quantity, decimal ReturnValue);

public sealed record PurchaseReturnView(
    Guid Id, string Code, Guid ReceiptId, string ReceiptCode, DateOnly ReturnDate, string Reason, decimal RefundAmount, PaymentMethod? RefundMethod,
    decimal DebtReductionAmount, decimal ReturnValue, IReadOnlyList<PurchaseReturnLineView> Lines);

/// <summary>
/// INP-05 – return goods of a Completed receipt to the supplier: refund received, debt reduction, or both.
/// Returned quantity never exceeds received − stocked − already returned, also under parallel returns
/// (the receipt's row version is part of the save).
/// </summary>
public sealed class PurchaseReturnService(
    IPurchaseReceiptRepository receipts,
    PurchaseReceiptViews views,
    ZoneAccessGuard zoneAccess,
    ICurrentUser currentUser,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const int MaxCodeAttempts = 5;

    public async Task<Result<PurchaseReceiptView>> CreateAsync(Guid receiptId, CreatePurchaseReturnRequest request, CancellationToken cancellationToken)
    {
        var receipt = await receipts.GetForEditAsync(receiptId, cancellationToken);
        if (receipt is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.PurchaseReceipts, PermissionAction.Create, receipt.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (receipt.StatusId != StatusIds.PurchaseReceipt.Completed)
        {
            return AppError.Conflict(UserMessages.CannotChangeStatus(
                "Purchase receipt", statusCatalog.DisplayNameOf(receipt.StatusId), "Returned", "only a Completed receipt can be returned"));
        }

        var invalid = new List<string>();
        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > 1000)
        {
            invalid.Add("Reason");
        }

        if (request.ReturnDate is not { } returnDate || returnDate < receipt.ReceiptDate || returnDate > timeProvider.Today())
        {
            invalid.Add("Return date");
        }

        var (refund, debtReduction) = (request.RefundAmount ?? 0, request.DebtReductionAmount ?? 0);
        if (refund > 0 && request.RefundMethod is null)
        {
            invalid.Add("Refund method");
        }

        if (refund < 0 || debtReduction < 0)
        {
            invalid.Add(refund < 0 ? "Refund amount" : "Debt reduction amount");
        }

        var lines = request.Lines ?? [];
        var receiptLines = receipt.Lines.ToDictionary(l => l.Id);
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].ReceiptLineId is not { } lineId || !receiptLines.ContainsKey(lineId))
            {
                invalid.Add($"Line {i + 1}");
            }
        }

        if (lines.Count == 0 || lines.Select(l => l.ReceiptLineId).Distinct().Count() != lines.Count)
        {
            invalid.Add("Lines");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid.Distinct().ToList());
        }

        if (await CheckQuantitiesAsync(receipt, lines, cancellationToken) is { } quantityError)
        {
            return quantityError;
        }

        var figures = PurchaseMoney.Of(receipt);
        var returnLines = lines.Select(l =>
        {
            var line = receiptLines[l.ReceiptLineId!.Value];
            return new PurchaseReturnLine
            {
                ReceiptLineId = line.Id,
                Quantity = l.Quantity!.Value,
                ReturnValue = PurchaseMoney.ReturnValue(l.Quantity!.Value, line.UnitPrice, figures.Payable, figures.Subtotal),
            };
        }).ToList();

        if (AmountError(refund, debtReduction, returnLines.Sum(l => l.ReturnValue), figures) is { } amountError)
        {
            return amountError;
        }

        var purchaseReturn = new PurchaseReturn
        {
            ReceiptId = receipt.Id,
            ReturnDate = request.ReturnDate!.Value,
            Reason = reason,
            RefundAmount = refund,
            RefundMethod = refund > 0 ? request.RefundMethod : null,
            DebtReductionAmount = debtReduction,
            RecordedByAccountId = currentUser.AccountId!.Value,
        };
        returnLines.ForEach(purchaseReturn.Lines.Add);
        receipt.Returns.Add(purchaseReturn);
        receipts.AddReturn(purchaseReturn);
        // The receipt row is part of the save, so a parallel return on the same receipt fails with MSG06.
        unitOfWork.MarkChanged(receipt);
        var recorded = audit.Record(new AuditEntry("PurchaseReturn.Created", nameof(PurchaseReturn))
        {
            RecordId = purchaseReturn.Id,
            ZoneId = receipt.ZoneId,
            After = new
            {
                ReceiptId = receipt.Id, receipt.Code, purchaseReturn.ReturnDate, Lines = returnLines.Select(l => new { l.ReceiptLineId, l.Quantity, l.ReturnValue }),
                purchaseReturn.RefundAmount, purchaseReturn.DebtReductionAmount,
            },
            Reason = reason,
        });
        await SaveWithCodeAsync(purchaseReturn, recorded, cancellationToken);
        return await views.ViewAsync(receipt.Id, cancellationToken);
    }

    public async Task<Result<PurchaseReturnView>> GetAsync(Guid returnId, CancellationToken cancellationToken)
    {
        var found = await receipts.GetReturnAsync(returnId, cancellationToken);
        if (found is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.PurchaseReceipts, PermissionAction.View, found.Receipt.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        return new PurchaseReturnView(
            found.Id, found.Code, found.ReceiptId, found.Receipt.Code, found.ReturnDate, found.Reason, found.RefundAmount, found.RefundMethod,
            found.DebtReductionAmount, found.Lines.Sum(l => l.ReturnValue),
            found.Lines.Select(l => new PurchaseReturnLineView(l.ReceiptLineId, l.ReceiptLine.Item.Code, l.ReceiptLine.Item.Name, l.ReceiptLine.Unit, l.Quantity, l.ReturnValue)).ToList());
    }

    private async Task<AppError?> CheckQuantitiesAsync(PurchaseReceipt receipt, IReadOnlyList<ReturnLineInput> lines, CancellationToken cancellationToken)
    {
        var stocked = await receipts.StockedAsync(receipt.Lines.Select(l => l.Id).ToList(), cancellationToken);
        var returned = receipt.Returns.SelectMany(r => r.Lines).GroupBy(l => l.ReceiptLineId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        for (var i = 0; i < lines.Count; i++)
        {
            var line = receipt.Lines.First(l => l.Id == lines[i].ReceiptLineId);
            if (lines[i].Quantity is not > 0)
            {
                return new AppError(ErrorKind.Validation, UserMessages.QuantityOrPriceInvalid, new Dictionary<string, object?> { ["field"] = $"Line {i + 1}" });
            }

            var (stockedQuantity, unknown) = stocked.GetValueOrDefault(line.Id);
            var returnable = PurchaseReceiptViews.Returnable(line, stockedQuantity, unknown, returned.GetValueOrDefault(line.Id));
            if (lines[i].Quantity > returnable)
            {
                return new AppError(
                    ErrorKind.Validation,
                    UserMessages.ExceedsLimit("Returned quantity", $"{returnable.ToString("0.###", CultureInfo.InvariantCulture)} {line.Unit}"),
                    new Dictionary<string, object?> { ["field"] = $"Line {i + 1}", ["limit"] = returnable });
            }
        }

        return null;
    }

    /// <summary>Refund + debt reduction = total return value; debt reduction ≤ outstanding; refund ≤ net paid.</summary>
    private static AppError? AmountError(decimal refund, decimal debtReduction, decimal total, ReceiptFigures figures)
    {
        foreach (var (amount, field) in new[] { (refund, "Refund amount"), (debtReduction, "Debt reduction amount") })
        {
            if (!PurchaseMoney.IsThousands(amount))
            {
                return new AppError(ErrorKind.Validation, UserMessages.NotThousands, new Dictionary<string, object?> { ["field"] = field });
            }
        }

        return refund + debtReduction != total ? PurchaseReceiptValidator.ExceedsLimit("Refund amount + Debt reduction amount", total)
            : debtReduction > figures.Outstanding ? PurchaseReceiptValidator.ExceedsLimit("Debt reduction amount", figures.Outstanding)
            : refund > figures.NetPaid ? PurchaseReceiptValidator.ExceedsLimit("Refund amount", figures.NetPaid)
            : null;
    }

    private async Task SaveWithCodeAsync(PurchaseReturn purchaseReturn, Domain.Entities.Platform.AuditEvent recorded, CancellationToken cancellationToken)
    {
        var prefix = DocumentCodes.PrefixFor(DocumentCodes.ReturnPrefix, timeProvider.Today());
        var used = await receipts.CountReturnCodesAsync(prefix, cancellationToken);
        for (var attempt = 0; ; attempt++)
        {
            purchaseReturn.Code = DocumentCodes.Next(prefix, used, attempt);
            recorded.RecordCode = purchaseReturn.Code;
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DuplicateRecordException) when (attempt < MaxCodeAttempts)
            {
            }
        }
    }
}
