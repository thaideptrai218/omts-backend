using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Purchasing.Items;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Purchasing;

/// <summary>The cost adjustment row of the stock card: written after the row that moved the cost and for a cost set by hand, never when the cost stays.</summary>
public sealed class InputStockLedgerCostAdjustmentTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 10, 9, 0, 0, TimeSpan.FromHours(7));

    private sealed class Account : ICurrentUser
    {
        public Guid? AccountId => null;
        public Guid? SessionId => null;
    }

    private sealed class Fixture(InputCostMethod method, decimal? cost)
    {
        public InMemoryStock Stock { get; } = new();
        public FarmingInputItem Rope { get; } = new() { Code = "VT000001", Name = "Dây", Group = InputItemGroup.Material, Unit = "m", CostPrice = cost };

        public InputStockLedger Ledger() => new(
            Stock,
            StubProxy.Create<IFarmingInputItemRepository>(h => h["GetByIdsForUpdateAsync"] = a => new List<FarmingInputItem> { Rope }.Where(i => ((IReadOnlyCollection<Guid>)a[0]!).Contains(i.Id)).ToList()),
            StubProxy.Create<ICooperativeRepository>(h => h["GetCurrentAsync"] = _ => new Cooperative { InputCostMethod = method }),
            StubProxy.Create<ISupplierRepository>(h => h["GetForEditAsync"] = _ => new Supplier { Name = "Công ty A" }),
            new Account(),
            TimeProvider.System);

        public void Stocked(decimal quantity) =>
            Stock.Add(new InputStockMovement { ItemId = Rope.Id, Kind = InputStockMovementKind.Opening, DocumentId = Rope.Id, DocumentCode = Rope.Code, OccurredAt = At, Quantity = quantity });

        public PurchaseReceipt Receipt(decimal quantity, decimal price) => new()
        {
            Code = "PN000001", ReceivedAt = At,
            Lines = [new PurchaseReceiptLine { LineNumber = 1, ItemId = Rope.Id, Quantity = quantity, EnteredQuantity = quantity, EnteredUnitPrice = price, UnitFactor = 1m }],
        };
    }

    [Fact]
    public async Task An_average_receipt_that_moves_the_cost_writes_a_cost_adjustment_right_after_its_row()
    {
        var f = new Fixture(InputCostMethod.Average, 100m);
        f.Stocked(10m);
        var receipt = f.Receipt(10m, 200m);

        await f.Ledger().PostReceiptAsync(receipt, CancellationToken.None);

        var rows = f.Stock.Rows.Where(r => r.Kind != InputStockMovementKind.Opening).OrderBy(r => r.Sequence).ToList();
        Assert.Equal([InputStockMovementKind.Receipt, InputStockMovementKind.CostAdjustment], rows.Select(r => r.Kind));
        var (movement, adjustment) = (rows[0], rows[1]);
        Assert.Equal(150m, f.Rope.CostPrice);
        Assert.Equal((150m, 10m), (movement.UnitCost!.Value, movement.Quantity));
        Assert.Equal((receipt.Id, "PN000001", At, 0m, 150m, 100m, movement.Id), (adjustment.DocumentId!.Value, adjustment.DocumentCode, adjustment.OccurredAt, adjustment.Quantity, adjustment.UnitCost!.Value, adjustment.PreviousUnitCost!.Value, adjustment.CausedByMovementId!.Value));
        Assert.Equal(10m + 10m, f.Stock.Rows.Sum(r => r.Quantity));
    }

    [Fact]
    public async Task A_fixed_first_receipt_that_sets_a_missing_cost_writes_an_adjustment_with_no_previous_cost()
    {
        var f = new Fixture(InputCostMethod.Fixed, null);

        await f.Ledger().PostReceiptAsync(f.Receipt(5m, 300m), CancellationToken.None);

        var adjustment = Assert.Single(f.Stock.Rows, r => r.Kind == InputStockMovementKind.CostAdjustment);
        Assert.Equal((300m, (decimal?)null), (adjustment.UnitCost!.Value, adjustment.PreviousUnitCost));
    }

    [Fact]
    public async Task A_posting_that_leaves_the_cost_as_it_is_writes_no_adjustment()
    {
        // Fixed keeps a cost that is set; Average at the same price keeps the average.
        var fixedCost = new Fixture(InputCostMethod.Fixed, 100m);
        await fixedCost.Ledger().PostReceiptAsync(fixedCost.Receipt(5m, 300m), CancellationToken.None);
        var average = new Fixture(InputCostMethod.Average, 200m);
        average.Stocked(10m);
        await average.Ledger().PostReceiptAsync(average.Receipt(10m, 200m), CancellationToken.None);

        Assert.DoesNotContain(fixedCost.Stock.Rows, r => r.Kind == InputStockMovementKind.CostAdjustment);
        Assert.DoesNotContain(average.Stock.Rows, r => r.Kind == InputStockMovementKind.CostAdjustment);
    }

    [Fact]
    public async Task Reversing_a_receipt_that_moved_the_cost_writes_its_own_adjustment_and_does_not_reverse_the_adjustment_row()
    {
        var f = new Fixture(InputCostMethod.Average, 100m);
        f.Stocked(10m);
        var receipt = f.Receipt(10m, 200m);
        await f.Ledger().PostReceiptAsync(receipt, CancellationToken.None);

        await f.Ledger().ReverseAsync(receipt.Id, afterReceiptRemoved: false, CancellationToken.None);

        var reversals = f.Stock.Rows.Where(r => r.Kind == InputStockMovementKind.Reversal).ToList();
        Assert.Single(reversals);
        Assert.Equal(-10m, reversals[0].Quantity);
        var adjustments = f.Stock.Rows.Where(r => r.Kind == InputStockMovementKind.CostAdjustment).OrderBy(r => r.Sequence).ToList();
        Assert.Equal(2, adjustments.Count);
        // 20 on hand at 150, 10 taken back at the price of the receipt (200): (20 × 150 − 10 × 200) / 10 = 100.
        Assert.Equal((150m, 100m), (adjustments[1].PreviousUnitCost!.Value, adjustments[1].UnitCost!.Value));
        Assert.Equal(reversals[0].Id, adjustments[1].CausedByMovementId);
        Assert.Equal(f.Rope.CostPrice, adjustments[1].UnitCost);
        Assert.Equal(10m, f.Stock.Rows.Sum(r => r.Quantity));
        Assert.DoesNotContain(f.Stock.Rows, r => r.ReversesMovementId is { } id && adjustments.Any(a => a.Id == id));
    }

    [Fact]
    public void A_manual_cost_change_writes_one_adjustment_for_the_document_with_no_quantity()
    {
        var f = new Fixture(InputCostMethod.Fixed, 100m);
        var receiptId = Guid.NewGuid();
        var previous = f.Rope.CostPrice;
        f.Rope.CostPrice = 120m;

        f.Ledger().RecordManualCostChange(f.Rope, previous, receiptId, "PN000007");

        var row = Assert.Single(f.Stock.Rows);
        Assert.Equal(
            (InputStockMovementKind.CostAdjustment, receiptId, "PN000007", 0m, 120m, (decimal?)100m, (Guid?)null),
            (row.Kind, row.DocumentId!.Value, row.DocumentCode, row.Quantity, row.UnitCost!.Value, row.PreviousUnitCost, row.CausedByMovementId));
    }

    [Fact]
    public void A_manual_save_with_the_same_cost_writes_nothing()
    {
        var f = new Fixture(InputCostMethod.Fixed, 100m);

        f.Ledger().RecordManualCostChange(f.Rope, 100m, f.Rope.Id, f.Rope.Code);

        Assert.Empty(f.Stock.Rows);
    }

    [Fact]
    public void A_manual_change_to_no_cost_is_recorded_with_the_old_cost()
    {
        var f = new Fixture(InputCostMethod.Fixed, 100m);
        f.Rope.CostPrice = null;

        f.Ledger().RecordManualCostChange(f.Rope, 100m, f.Rope.Id, f.Rope.Code);

        var row = Assert.Single(f.Stock.Rows);
        Assert.Equal((null, 100m), (row.UnitCost, row.PreviousUnitCost));
    }

    [Fact]
    public async Task A_cost_adjustment_row_is_left_out_when_an_item_document_is_reversed()
    {
        // The item edit writes its adjustment under the item id, like the opening: undoing the opening must not touch it.
        var f = new Fixture(InputCostMethod.Fixed, 100m);
        f.Stocked(4m);
        f.Rope.CostPrice = 110m;
        var ledger = f.Ledger();
        ledger.RecordManualCostChange(f.Rope, 100m, f.Rope.Id, f.Rope.Code);

        await ledger.ReverseAsync(f.Rope.Id, afterReceiptRemoved: false, CancellationToken.None);

        Assert.Single(f.Stock.Rows, r => r.Kind == InputStockMovementKind.Reversal);
        Assert.Single(f.Stock.Rows, r => r.Kind == InputStockMovementKind.CostAdjustment);
        Assert.True(await f.Stock.HasOnlyOpeningAsync(f.Rope.Id, CancellationToken.None));
    }
}
