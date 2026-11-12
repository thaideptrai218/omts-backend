using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Features.Purchasing.Items;

/// <summary>
/// "Thiết lập giá" of a receipt: the items on its lines with the price of this receipt, their cost price and their last purchase price (all per base unit), and the Owner's bulk update of
/// the cost prices. Reading follows the right to view receipts (API filter), the update is Owner only. The update records no movement: the cost of past movements stays.
/// </summary>
public sealed class ReceiptPriceSetupService(
    IPurchaseReceiptRepository receipts, IFarmingInputItemRepository items, AuditLogService audit, IUnitOfWork unitOfWork)
{
    private const decimal MaxCost = 1_000_000_000_000m;

    public async Task<Result<ReceiptPriceSetupView>> GetAsync(Guid receiptId, CancellationToken cancellationToken)
    {
        var receipt = await receipts.GetForEditAsync(receiptId, cancellationToken);
        return receipt is null ? AppError.NotFound : await ViewAsync(receipt, cancellationToken);
    }

    public async Task<Result<ReceiptPriceSetupView>> UpdateAsync(Guid receiptId, UpdateReceiptPriceSetupRequest request, CancellationToken cancellationToken)
    {
        var receipt = await receipts.GetForEditAsync(receiptId, cancellationToken);
        if (receipt is null)
        {
            return AppError.NotFound;
        }

        var inputs = request.Items ?? [];
        var onReceipt = receipt.Lines.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value).ToHashSet();
        var invalid = new List<string>();
        for (var i = 0; i < inputs.Count; i++)
        {
            if (inputs[i]?.ItemId is not { } itemId || !onReceipt.Contains(itemId)
                || (inputs[i].CostPrice is { } cost && (cost < 0 || cost > MaxCost || decimal.Round(cost, 4) != cost)))
            {
                invalid.Add($"Item {i + 1}");
            }
        }

        if (inputs.Count == 0 || inputs.Select(i => i?.ItemId).Distinct().Count() != inputs.Count)
        {
            invalid.Add("Items");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var tracked = (await items.GetByIdsForUpdateAsync(inputs.Select(i => i.ItemId!.Value).ToList(), cancellationToken)).ToDictionary(i => i.Id);
        foreach (var input in inputs)
        {
            var item = tracked[input.ItemId!.Value];
            if (item.CostPrice == input.CostPrice)
            {
                continue;
            }

            audit.Record(new AuditEntry("FarmingInputItem.Updated", nameof(FarmingInputItem))
            {
                RecordId = item.Id, RecordCode = item.Code, Before = new { item.CostPrice }, After = new { input.CostPrice },
            });
            item.CostPrice = input.CostPrice;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ViewAsync(receipt, cancellationToken);
    }

    private async Task<ReceiptPriceSetupView> ViewAsync(PurchaseReceipt receipt, CancellationToken cancellationToken)
    {
        var current = (await items.GetByIdsAsync(receipt.Lines.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value).Distinct().ToList(), cancellationToken))
            .ToDictionary(i => i.Id);
        var lines = new List<ReceiptPriceSetupLine>();
        foreach (var line in receipt.Lines.OrderBy(l => l.LineNumber))
        {
            if (line.ItemId is not { } itemId || !current.TryGetValue(itemId, out var item) || lines.Any(l => l.ItemId == itemId))
            {
                continue;
            }

            lines.Add(new ReceiptPriceSetupLine(
                item.Id, item.Code, item.Name, item.Unit, line.EnteredUnitPrice is null ? null : ReceiptLineUnits.NetPricePerBase(line), item.CostPrice, item.LastPurchasePrice));
        }

        return new ReceiptPriceSetupView(receipt.Id, receipt.Code, lines);
    }
}
