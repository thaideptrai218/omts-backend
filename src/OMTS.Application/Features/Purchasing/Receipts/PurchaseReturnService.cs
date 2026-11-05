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
using OMTS.Application.Features.Purchasing.Payments;
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
/// Returned quantity never exceeds received − seeded − already returned, also under parallel returns
/// (the receipt's row version is part of the save). Receipts have no zone: the API checks the role (BR-FARM-08).
/// The SRS 27/10 rework of returns (PURCHASE-02) is a later PR.
/// </summary>
public sealed class PurchaseReturnService(
    IPurchaseReceiptRepository receipts,
    PurchaseReceiptViews views,
    SupplierDebtService debts,
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

        if (receipt.StatusId != StatusIds.PurchaseReceipt.Completed)
        {
            // Only a Completed receipt can be returned.
            return AppError.Conflict(UserMessages.WrongStatus($"Purchase receipt {receipt.Code}", statusCatalog.DisplayNameOf(receipt.StatusId)));
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
        if ((refund > 0 && request.RefundMethod is null) || (request.RefundMethod is { } method && !Enum.IsDefined(method)))
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

        var seeded = await receipts.SeededAsync(receipt.Lines.Select(l => l.Id).ToList(), cancellationToken);
        var returned = PurchaseReceiptViews.ReturnedQuantities(receipt);
        if (CheckQuantities(receipt, lines, seeded, returned) is { } quantityError)
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
                ReturnValue = PurchaseMoney.CappedReturnValue(
                    l.Quantity!.Value, line, receipt.Returns.SelectMany(r => r.Lines).Where(x => x.ReceiptLineId == line.Id).Sum(x => x.ReturnValue)),
            };
        }).ToList();

        // Rounded line values can add up to more than the total: fit the return to what the receipt has left (in thousands).
        // Exactly the rest only when this return brings back everything received: goods already seeded stay paid for.
        var nothingLeft = receipt.Lines.All(l =>
            returned.GetValueOrDefault(l.Id) + lines.Where(x => x.ReceiptLineId == l.Id).Sum(x => x.Quantity!.Value) >= (l.Quantity ?? 0));
        var fitted = PurchaseMoney.FitToReceipt(
            returnLines.Select(l => l.ReturnValue).ToList(), PurchaseMoney.RoundToThousand(figures.Total) - figures.Returned, nothingLeft);
        for (var i = 0; i < returnLines.Count; i++)
        {
            returnLines[i].ReturnValue = fitted[i];
        }

        if (AmountError(refund, debtReduction, returnLines.Sum(l => l.ReturnValue), await debts.SumsAsync(receipt.SupplierId, cancellationToken)) is { } amountError)
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

        return new PurchaseReturnView(
            found.Id, found.Code, found.ReceiptId, found.Receipt.Code, found.ReturnDate, found.Reason, found.RefundAmount, found.RefundMethod,
            found.DebtReductionAmount, found.Lines.Sum(l => l.ReturnValue),
            found.Lines.Select(l => new PurchaseReturnLineView(
                l.ReceiptLineId, l.ReceiptLine.Item?.Code ?? string.Empty, l.ReceiptLine.Item?.Name ?? string.Empty, l.ReceiptLine.Unit, l.Quantity, l.ReturnValue)).ToList());
    }

    private static AppError? CheckQuantities(
        PurchaseReceipt receipt, IReadOnlyList<ReturnLineInput> lines, IReadOnlyDictionary<Guid, (decimal Seeded, bool UnknownQuantity)> seeded, IReadOnlyDictionary<Guid, decimal> returned)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = receipt.Lines.First(l => l.Id == lines[i].ReceiptLineId);
            if (lines[i].Quantity is null)
            {
                var field = $"Line {i + 1}";
                return new AppError(
                    ErrorKind.Validation, UserMessages.Invalid(field) with { LegacyCode = MessageCodes.Legacy.Msg59 }, new Dictionary<string, object?> { ["field"] = field });
            }

            var (seededQuantity, unknown) = seeded.GetValueOrDefault(line.Id);
            var lineReturned = returned.GetValueOrDefault(line.Id);
            var returnable = PurchaseReceiptViews.Returnable(line, seededQuantity, unknown, lineReturned);
            // MSG-PURCHASE-03 with the limit of the line (MSG59 for a quantity not greater than 0, MSG45 above the limit before).
            if (lines[i].Quantity is not > 0 || lines[i].Quantity > returnable)
            {
                var seededText = seededQuantity > 0 ? $", seeded {Quantity(seededQuantity)} {line.Unit}" : string.Empty;
                var explanation = unknown
                    ? "The quantity already seeded from this line is not known."
                    : $"Received {Quantity(line.Quantity ?? 0)} {line.Unit}{seededText}, already returned {Quantity(lineReturned)} {line.Unit}.";
                return new AppError(
                    ErrorKind.Validation,
                    UserMessages.ReturnQuantityOutsideLimit(
                        line.Item?.Name ?? string.Empty, Quantity(returnable), line.Unit, explanation,
                        lines[i].Quantity is not > 0 ? MessageCodes.Legacy.Msg59 : MessageCodes.Legacy.Msg45),
                    new Dictionary<string, object?> { ["field"] = $"Line {i + 1}", ["limit"] = returnable });
            }
        }

        return null;
    }

    private static string Quantity(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Refund + debt reduction = total return value; debt reduction ≤ the supplier debt; refund ≤ what the supplier holds.
    /// Payments are not linked to receipts any more, so the limits are the supplier's (until P4 drops the split).
    /// </summary>
    private static AppError? AmountError(decimal refund, decimal debtReduction, decimal total, SupplierDebtSums supplier)
    {
        foreach (var (amount, field) in new[] { (refund, "Refund amount"), (debtReduction, "Debt reduction amount") })
        {
            if (!PurchaseMoney.IsThousands(amount))
            {
                return new AppError(ErrorKind.Validation, UserMessages.NotThousands, new Dictionary<string, object?> { ["field"] = field });
            }
        }

        var (debtLimit, refundLimit) = (ThousandsLimit(supplier.Debt), ThousandsLimit(supplier.Credit));
        return refund + debtReduction != total ? PurchaseReceiptValidator.ExceedsLimit("Refund amount + Debt reduction amount", total)
            : debtReduction > debtLimit ? PurchaseReceiptValidator.ExceedsLimit("Debt reduction amount", debtLimit)
            : refund > refundLimit ? PurchaseReceiptValidator.ExceedsLimit("Refund amount", refundLimit)
            : null;
    }

    /// <summary>
    /// A limit for amounts in whole thousands, now that debts and payments keep their dongs: up to the next thousand, so a
    /// return worth 1,000 of a receipt of 1,497 with 997 paid still fits (500 of debt + 997 held can add up to 1,000).
    /// </summary>
    private static decimal ThousandsLimit(decimal limit) => Math.Max(0, Math.Ceiling(limit / PurchaseMoney.Thousand) * PurchaseMoney.Thousand);

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
