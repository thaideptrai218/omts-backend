using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Purchasing;
using OMTS.Application.Features.Purchasing.Items;
using OMTS.Application.Features.Purchasing.Payments;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Application.Features.Reports.Purchases;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Purchasing;

/// <summary>Input VAT on purchase receipts and returns: the rates, the VAT of a line with the receipt discount in it, the refund on a return, the cost with VAT and the supplier debt.</summary>
public sealed class InputVatTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 28, 9, 0, 0, TimeSpan.FromHours(7));
    private static readonly DateOnly Today = new(2026, 11, 28);

    private static PurchaseReceipt Receipt(decimal discount, params (decimal Amount, decimal? Percent)[] lines)
    {
        var receipt = new PurchaseReceipt { Discount = discount, StatusId = StatusIds.PurchaseReceipt.Completed };
        var number = 0;
        foreach (var (amount, percent) in lines)
        {
            receipt.Lines.Add(new PurchaseReceiptLine { LineNumber = ++number, LineAmount = amount, VatPercent = percent });
        }

        PurchaseMoney.ApplyVat(receipt);
        return receipt;
    }

    // ---- the VAT of a line ----

    [Theory]
    [InlineData(100_000, 0, 10d, 10_000)]
    [InlineData(100_000, 10_000, 10d, 9_000)]
    [InlineData(100_000, 0, 8d, 8_000)]
    [InlineData(100_000, 0, 5d, 5_000)]
    [InlineData(100_000, 0, 0d, 0)]
    [InlineData(100_000, 0, null, 0)]
    public void The_VAT_of_a_line_is_its_amount_less_its_share_of_the_receipt_discount_times_the_rate(decimal amount, decimal discount, double? percent, decimal expected) =>
        Assert.Equal(expected, PurchaseMoney.LineVat(amount, amount, discount, (decimal?)percent));

    [Theory]
    [InlineData(5, 0.5, 1)]
    [InlineData(15, 1.5, 2)]
    [InlineData(14, 1.4, 1)]
    [InlineData(4, 0.4, 0)]
    public void The_VAT_is_rounded_half_up_to_the_dong(decimal amount, decimal exactVat, decimal expected)
    {
        Assert.Equal(exactVat, amount * 0.10m);
        Assert.Equal(expected, PurchaseMoney.LineVat(amount, amount, 0, 10m));
    }

    [Fact]
    public void The_discount_of_the_receipt_is_shared_by_line_amount_and_every_line_has_its_own_rate()
    {
        // 60,000 at 10% and 40,000 at KCT, 10,000 off: the first line carries 54,000 × 10% = 5,400, the second none.
        var receipt = Receipt(10_000, (60_000m, 10m), (40_000m, null));

        Assert.Equal([5_400m, 0m], receipt.Lines.OrderBy(l => l.LineNumber).Select(l => l.VatAmount));
        var figures = PurchaseMoney.Of(receipt);
        Assert.Equal((100_000m, 5_400m, 95_400m), (figures.Total, figures.Vat, figures.Payable));
    }

    [Fact]
    public void The_share_of_the_discount_is_exact_before_rounding_so_each_line_rounds_once()
    {
        // 3 lines of 33,333 with 10 off: each 33,333 × 99,989 / 99,999 × 8% = 2,666.4 -> 2,666 (not rounded twice).
        var receipt = Receipt(10, (33_333m, 8m), (33_333m, 8m), (33_333m, 8m));

        Assert.All(receipt.Lines, l => Assert.Equal(PurchaseMoney.RoundHalfUp(33_333m * 99_989m * 8m / (99_999m * 100m)), l.VatAmount));
        Assert.Equal(7_998m, PurchaseMoney.Vat(receipt.Lines));
    }

    [Fact]
    public void A_receipt_of_one_phao_at_100000_and_ten_percent_is_owed_110000_and_a_discount_of_10000_leaves_99000()
    {
        var plain = Receipt(0, (100_000m, 10m));
        Assert.Equal((10_000m, 110_000m), (PurchaseMoney.Vat(plain.Lines), PurchaseMoney.Of(plain).Payable));

        var discounted = Receipt(10_000, (100_000m, 10m));
        Assert.Equal((9_000m, 99_000m), (PurchaseMoney.Vat(discounted.Lines), PurchaseMoney.Of(discounted).Payable));
    }

    [Fact]
    public void A_line_made_before_input_VAT_has_no_rate_and_no_VAT_so_the_figures_stay()
    {
        var old = Receipt(500, (10_000m, null), (2_000m, null));

        var figures = PurchaseMoney.Of(old);

        Assert.Equal((12_000m, 0m, 11_500m), (figures.Total, figures.Vat, figures.Payable));
        Assert.All(old.Lines, l => Assert.Null(l.VatRateId));
    }

    [Fact]
    public void Applying_the_VAT_again_after_the_discount_or_a_rate_changes_gives_the_new_figures()
    {
        var receipt = Receipt(0, (100_000m, 10m));

        receipt.Discount = 10_000;
        receipt.Lines.Single().VatPercent = 8m;
        PurchaseMoney.ApplyVat(receipt);

        Assert.Equal(7_200m, receipt.Lines.Single().VatAmount);
    }

    [Fact]
    public void A_receipt_with_no_amount_yet_has_no_VAT()
    {
        var draft = Receipt(0, (0m, 10m));
        Assert.Equal(0m, draft.Lines.Single().VatAmount);
        Assert.Equal(0m, PurchaseMoney.LineVat(100m, 0m, 0m, 10m));
    }

    // ---- which rate a line gets ----

    private readonly IReadOnlyDictionary<Guid, VatRate> Rates = VatKit.Catalogue();

    private static FarmingInputItem Supply(Guid rateId, InputItemGroup group = InputItemGroup.Material) => new() { Group = group, PurchaseVatRateId = rateId };

    private static PurchaseReceiptLine Taken(FarmingInputItem item, VatRate? rate)
    {
        var line = new PurchaseReceiptLine { ItemId = item.Id };
        if (rate is not null)
        {
            VatSnapshot.Of(rate).ApplyTo(line);
        }

        return line;
    }

    [Fact]
    public void A_new_line_takes_the_rate_sent_else_the_current_rate_of_its_item_and_a_seed_line_is_always_KCT()
    {
        var phao = Supply(VatRateIds.Ten);
        var seed = Supply(VatRateIds.Kct, InputItemGroup.Seed);

        Assert.Equal(new VatSnapshot(VatRateIds.Ten, "10%", 10m), VatSnapshot.ForReceiptLine(null, phao, kept: null, Rates));
        Assert.Equal(new VatSnapshot(VatRateIds.Eight, "8%", 8m), VatSnapshot.ForReceiptLine(VatRateIds.Eight, phao, kept: null, Rates));
        Assert.Equal(new VatSnapshot(VatRateIds.Kct, "KCT", null), VatSnapshot.ForReceiptLine(VatRateIds.Ten, seed, kept: null, Rates));
        Assert.Equal(new VatSnapshot(VatRateIds.Kct, "KCT", null), VatSnapshot.ForReceiptLine(null, seed, kept: null, Rates));
    }

    [Fact]
    public void A_new_line_reads_the_percent_the_rate_has_now_and_a_kept_line_keeps_the_percent_it_took()
    {
        var phao = Supply(VatRateIds.Ten);
        var kept = Taken(phao, Rates[VatRateIds.Ten]);
        VatKit.Edit(Rates, VatRateIds.Ten, "10%", 12m);

        // The rate was edited to 12% afterwards: the kept line still says 10, a new line (or another item) says 12.
        Assert.Equal(new VatSnapshot(VatRateIds.Ten, "10%", 10m), VatSnapshot.ForReceiptLine(null, phao, kept, Rates));
        Assert.Equal(new VatSnapshot(VatRateIds.Ten, "10%", 10m), VatSnapshot.ForReceiptLine(VatRateIds.Ten, phao, kept, Rates));
        Assert.Equal(new VatSnapshot(VatRateIds.Ten, "10%", 12m), VatSnapshot.ForReceiptLine(null, phao, kept: null, Rates));
        Assert.Equal(new VatSnapshot(VatRateIds.Ten, "10%", 12m), VatSnapshot.ForReceiptLine(null, phao, new PurchaseReceiptLine { ItemId = Guid.NewGuid() }, Rates));
    }

    [Fact]
    public void A_kept_line_given_another_rate_takes_that_rate_as_it_is_now()
    {
        var phao = Supply(VatRateIds.Ten);
        var kept = Taken(phao, Rates[VatRateIds.Ten]);

        Assert.Equal(new VatSnapshot(VatRateIds.Five, "5%", 5m), VatSnapshot.ForReceiptLine(VatRateIds.Five, phao, kept, Rates));
        Assert.Equal(new VatSnapshot(VatRateIds.Kct, "KCT", null), VatSnapshot.ForReceiptLine(VatRateIds.Kct, phao, kept, Rates));
    }

    [Fact]
    public void A_kept_line_of_the_same_item_sent_without_a_rate_keeps_its_snapshot_and_none_stays_none_so_an_old_receipt_is_not_taxed_by_an_edit()
    {
        var phao = Supply(VatRateIds.Ten);
        var old = Taken(phao, null);
        var eight = Taken(phao, Rates[VatRateIds.Eight]);

        Assert.Equal(VatSnapshot.None, VatSnapshot.ForReceiptLine(null, phao, old, Rates));
        Assert.Equal(new VatSnapshot(VatRateIds.Eight, "8%", 8m), VatSnapshot.ForReceiptLine(null, phao, eight, Rates));
        // Another item on the line takes that item's rate.
        Assert.Equal(new VatSnapshot(VatRateIds.Five, "5%", 5m), VatSnapshot.ForReceiptLine(null, Supply(VatRateIds.Five), eight, Rates));
    }

    [Fact]
    public void An_old_receipt_saved_again_keeps_its_figures()
    {
        var phao = Supply(VatRateIds.Ten);
        var receipt = Receipt(0, (100_000m, null));
        var line = receipt.Lines.Single();
        line.ItemId = phao.Id;

        VatSnapshot.ForReceiptLine(null, phao, line, Rates).ApplyTo(line);
        PurchaseMoney.ApplyVat(receipt);

        Assert.Null(line.VatRateId);
        Assert.Equal((0m, 100_000m), (PurchaseMoney.Vat(receipt.Lines), PurchaseMoney.Of(receipt).Payable));
    }

    [Fact]
    public void An_edit_of_the_percent_of_a_rate_does_not_change_the_VAT_of_a_receipt_that_exists_but_the_next_receipt_uses_it()
    {
        var phao = Supply(VatRateIds.Ten);
        var first = Receipt(0, (100_000m, 10m));
        var firstLine = first.Lines.Single();
        VatSnapshot.Of(Rates[VatRateIds.Ten]).ApplyTo(firstLine);
        firstLine.ItemId = phao.Id;

        VatKit.Edit(Rates, VatRateIds.Ten, "10%", 8m);

        // The first receipt is saved again (an edit of its discount): the line keeps 10%, so the VAT is still 10,000.
        first.Discount = 0;
        VatSnapshot.ForReceiptLine(null, phao, firstLine, Rates).ApplyTo(firstLine);
        PurchaseMoney.ApplyVat(first);
        Assert.Equal(10_000m, firstLine.VatAmount);

        // A new receipt takes the item's rate as it is now.
        var second = Receipt(0, (100_000m, null));
        var secondLine = second.Lines.Single();
        VatSnapshot.ForReceiptLine(null, phao, kept: null, Rates).ApplyTo(secondLine);
        PurchaseMoney.ApplyVat(second);
        Assert.Equal((8m, 8_000m), (secondLine.VatPercent!.Value, secondLine.VatAmount));
    }

    [Fact]
    public void A_rate_that_is_not_in_the_list_or_is_inactive_is_refused_unless_the_stored_line_already_has_it()
    {
        var phao = Supply(VatRateIds.Ten);
        var seed = Supply(VatRateIds.Kct, InputItemGroup.Seed);
        var catalogue = VatKit.Catalogue();
        catalogue[VatRateIds.Five].IsActive = false;

        Assert.False(PurchaseReceiptValidator.VatRateRejected(null, phao, null, catalogue));
        Assert.False(PurchaseReceiptValidator.VatRateRejected(VatRateIds.Eight, phao, null, catalogue));
        Assert.True(PurchaseReceiptValidator.VatRateRejected(Guid.NewGuid(), phao, null, catalogue));
        Assert.True(PurchaseReceiptValidator.VatRateRejected(VatRateIds.Five, phao, null, catalogue));
        Assert.False(PurchaseReceiptValidator.VatRateRejected(VatRateIds.Five, phao, VatRateIds.Five, catalogue));
        // A seed line is always KCT, so what is sent for it is not looked at.
        Assert.False(PurchaseReceiptValidator.VatRateRejected(Guid.NewGuid(), seed, null, catalogue));
    }

    // ---- returns ----

    [Fact]
    public void A_return_line_gives_back_the_VAT_of_the_rate_it_holds_on_its_recorded_value()
    {
        var line = new PurchaseReturnLine { VatPercent = 10m };

        ReceiptLineUnits.FillReturn(line, "cái", null, 1m, 1m, 100_000m);

        Assert.Equal((100_000m, 10_000m), (line.ReturnValue, line.VatAmount));
        var kct = new PurchaseReturnLine { VatPercent = null };
        ReceiptLineUnits.FillReturn(kct, "cái", null, 1m, 1m, 100_000m);
        var old = new PurchaseReturnLine();
        ReceiptLineUnits.FillReturn(old, "cái", null, 1m, 1m, 100_000m);
        Assert.Equal((0m, 0m), (kct.VatAmount, old.VatAmount));
    }

    [Fact]
    public void The_refund_of_a_return_is_its_lines_and_their_VAT_less_its_discount_rounded_half_up_per_line()
    {
        var purchaseReturn = new PurchaseReturn { Discount = 1_000 };
        var rope = new PurchaseReturnLine { VatPercent = 8m };
        var buoy = new PurchaseReturnLine { VatPercent = 10m };
        // 1.5 × 3,333 = 4,999.5 -> 5,000 at 8% = 400; 15 at 10% = 1.5 -> 2.
        ReceiptLineUnits.FillReturn(rope, "m", null, 1m, 1.5m, 3_333m);
        ReceiptLineUnits.FillReturn(buoy, "cái", null, 1m, 1m, 15m);
        purchaseReturn.Lines.Add(rope);
        purchaseReturn.Lines.Add(buoy);

        Assert.Equal((5_000m, 400m), (rope.ReturnValue, rope.VatAmount));
        Assert.Equal((15m, 2m), (buoy.ReturnValue, buoy.VatAmount));
        Assert.Equal(402m, PurchaseMoney.ReturnVat(purchaseReturn));
        Assert.Equal(5_000m + 15m + 402m - 1_000m, PurchaseMoney.ReturnNet(purchaseReturn));
    }

    [Fact]
    public void A_recorded_return_of_the_phao_clears_the_110000_the_receipt_added_to_the_debt_and_a_cancelled_one_counts_for_nothing()
    {
        var receipt = Receipt(0, (100_000m, 10m));
        var line = receipt.Lines.Single();
        var recorded = new PurchaseReturn { StatusId = StatusIds.PurchaseReturn.Recorded };
        var phao = new PurchaseReturnLine { ReceiptLineId = line.Id, VatPercent = line.VatPercent };
        ReceiptLineUnits.FillReturn(phao, "cái", null, 1m, 1m, 100_000m);
        recorded.Lines.Add(phao);
        receipt.Returns.Add(recorded);
        var cancelled = new PurchaseReturn { StatusId = StatusIds.PurchaseReturn.Cancelled };
        var again = new PurchaseReturnLine { ReceiptLineId = line.Id, VatPercent = line.VatPercent };
        ReceiptLineUnits.FillReturn(again, "cái", null, 1m, 1m, 100_000m);
        cancelled.Lines.Add(again);
        receipt.Returns.Add(cancelled);

        var figures = PurchaseMoney.Of(receipt);

        Assert.Equal((110_000m, 110_000m, 0m), (figures.Payable, figures.Returned, figures.DebtShare));
        Assert.Equal(10_000m, PurchaseMoney.ReturnVat(recorded));
        Assert.Equal(110_000m, PurchaseMoney.ReturnNet(recorded));
    }

    // ---- cost ----

    [Fact]
    public void The_cost_of_a_base_unit_is_its_net_price_with_the_VAT_of_the_line()
    {
        var phao = new PurchaseReceiptLine { VatPercent = 10m, UnitFactor = 1m, EnteredUnitPrice = 100_000m };
        var discounted = new PurchaseReceiptLine { VatPercent = 10m, UnitFactor = 1m, EnteredUnitPrice = 100_000m, EnteredDiscount = 5_000m };
        var box = new PurchaseReceiptLine { VatPercent = 8m, UnitFactor = 12m, EnteredUnitPrice = 120_000m };
        var kct = new PurchaseReceiptLine { VatPercent = null, UnitFactor = 1m, EnteredUnitPrice = 100_000m };
        var old = new PurchaseReceiptLine { UnitFactor = 1m, EnteredUnitPrice = 100_000m };

        Assert.Equal(110_000m, ReceiptLineUnits.CostPerBase(phao));
        Assert.Equal(104_500m, ReceiptLineUnits.CostPerBase(discounted));
        Assert.Equal(10_800m, ReceiptLineUnits.CostPerBase(box));
        Assert.Equal(100_000m, ReceiptLineUnits.CostPerBase(kct));
        Assert.Equal(100_000m, ReceiptLineUnits.CostPerBase(old));
        Assert.Equal(1.1m, ReceiptLineUnits.WithVat(1m, 10m));
    }

    private sealed class Account : ICurrentUser
    {
        public Guid? AccountId => null;
        public Guid? SessionId => null;
    }

    private sealed class Fixture(InputCostMethod method, decimal? cost)
    {
        public InMemoryStock Stock { get; } = new();
        public FarmingInputItem Phao { get; } = new() { Code = "VT000001", Name = "Phao", Group = InputItemGroup.Material, Unit = "cái", CostPrice = cost };

        public InputStockLedger Ledger() => new(
            Stock,
            StubProxy.Create<IFarmingInputItemRepository>(h => h["GetByIdsForUpdateAsync"] = a => new List<FarmingInputItem> { Phao }.Where(i => ((IReadOnlyCollection<Guid>)a[0]!).Contains(i.Id)).ToList()),
            StubProxy.Create<ICooperativeRepository>(h => h["GetCurrentAsync"] = _ => new Cooperative { InputCostMethod = method }),
            StubProxy.Create<ISupplierRepository>(h => h["GetForEditAsync"] = _ => new Supplier { Name = "Công ty A" }),
            new Account(),
            TimeProvider.System);

        public void Stocked(decimal quantity) =>
            Stock.Add(new InputStockMovement { ItemId = Phao.Id, Kind = InputStockMovementKind.Opening, DocumentId = Phao.Id, DocumentCode = Phao.Code, OccurredAt = At, Quantity = quantity });

        public PurchaseReceipt Receipt(decimal quantity, decimal price, decimal? percent)
        {
            var receipt = new PurchaseReceipt
            {
                Code = "PN000001", ReceivedAt = At,
                Lines = [new PurchaseReceiptLine { LineNumber = 1, ItemId = Phao.Id, Quantity = quantity, EnteredQuantity = quantity, EnteredUnitPrice = price, UnitFactor = 1m, VatPercent = percent }],
            };
            return receipt;
        }

        public PurchaseReturn Return(decimal quantity, decimal price, decimal? percent)
        {
            var line = new PurchaseReturnLine { ItemId = Phao.Id, VatPercent = percent };
            ReceiptLineUnits.FillReturn(line, "cái", null, 1m, quantity, price);
            return new PurchaseReturn { Code = "TH000001", ReturnedAt = At, Lines = [line] };
        }
    }

    [Fact]
    public async Task A_receipt_of_the_phao_with_no_stock_sets_the_cost_and_the_stock_card_price_to_110000()
    {
        var f = new Fixture(InputCostMethod.Average, null);

        await f.Ledger().PostReceiptAsync(f.Receipt(1m, 100_000m, 10m), CancellationToken.None);

        var row = Assert.Single(f.Stock.Rows, r => r.Kind == InputStockMovementKind.Receipt);
        Assert.Equal((110_000m, 110_000m), (row.TransactionPrice!.Value, f.Phao.CostPrice!.Value));
        Assert.Equal(110_000m, f.Phao.LastPurchasePrice);
    }

    [Fact]
    public async Task The_fixed_cost_of_a_first_receipt_is_the_price_with_VAT()
    {
        var f = new Fixture(InputCostMethod.Fixed, null);

        await f.Ledger().PostReceiptAsync(f.Receipt(2m, 50_000m, 8m), CancellationToken.None);

        Assert.Equal(54_000m, f.Phao.CostPrice);
    }

    [Fact]
    public async Task The_average_cost_counts_the_VAT_of_a_receipt_and_of_the_return_that_reverses_it()
    {
        var f = new Fixture(InputCostMethod.Average, 100m);
        f.Stocked(10m);

        // 10 on hand at 100, 10 received at 200 + 10% = 220: (1,000 + 2,200) / 20 = 160.
        await f.Ledger().PostReceiptAsync(f.Receipt(10m, 200m, 10m), CancellationToken.None);
        Assert.Equal(160m, f.Phao.CostPrice);

        // 5 go back at 200 + 10% = 220: (20 × 160 − 5 × 220) / 15 = 140.
        await f.Ledger().PostReturnAsync(f.Return(5m, 200m, 10m), CancellationToken.None);
        var returnRow = f.Stock.Rows.Single(r => r.Kind == InputStockMovementKind.PurchaseReturn);
        Assert.Equal((-5m, 220m), (returnRow.Quantity, returnRow.TransactionPrice!.Value));
        Assert.Equal(140m, f.Phao.CostPrice);
    }

    [Fact]
    public async Task A_line_with_no_rate_posts_at_its_price_as_before()
    {
        var f = new Fixture(InputCostMethod.Average, 100m);
        f.Stocked(10m);

        await f.Ledger().PostReceiptAsync(f.Receipt(10m, 200m, null), CancellationToken.None);

        Assert.Equal(150m, f.Phao.CostPrice);
    }

    [Fact]
    public async Task The_edit_of_a_receipt_rate_is_undone_at_the_old_cost_and_posted_at_the_new_one()
    {
        var f = new Fixture(InputCostMethod.Average, null);
        var receipt = f.Receipt(1m, 100_000m, 10m);
        await f.Ledger().PostReceiptAsync(receipt, CancellationToken.None);
        Assert.Equal(110_000m, f.Phao.CostPrice);

        receipt.Lines.Single().VatPercent = 8m;
        var ledger = f.Ledger();
        await ledger.ReverseAsync(receipt.Id, afterReceiptRemoved: false, CancellationToken.None);
        await ledger.PostReceiptAsync(receipt, CancellationToken.None);

        Assert.Equal(108_000m, f.Phao.CostPrice);
        Assert.Equal(1m, f.Stock.Rows.Sum(r => r.Quantity));
    }

    // ---- the supplier debt ----

    [Fact]
    public void A_receipt_owes_its_total_less_its_discount_plus_its_VAT_less_its_returns()
    {
        var receipt = new DebtReceiptRow(Guid.Empty, Guid.NewGuid(), "PN000001", Today.AddDays(-1), Total: 100_000, Returned: 0, Discount: 10_000, Vat: 9_000);

        var unpaid = Assert.Single(SupplierDebtLedger.UnpaidReceipts([receipt], 0, Today));

        Assert.Equal(99_000m, receipt.Net);
        Assert.Equal((99_000m, 9_000m, 10_000m), (unpaid.Remaining, unpaid.Vat, unpaid.Discount));
    }

    [Fact]
    public void A_return_with_its_VAT_given_back_lowers_the_receipt_and_the_debt_by_the_same_amount()
    {
        var receipt = new DebtReceiptRow(Guid.Empty, Guid.NewGuid(), "PN000001", Today.AddDays(-1), Total: 100_000, Returned: 110_000, Vat: 10_000);
        var sums = new SupplierDebtSums(Receipts: 110_000, Returned: 110_000, Paid: 0, PaidBack: 0);

        Assert.Equal(0m, receipt.Net);
        Assert.Equal(0m, sums.Debt);
        Assert.Empty(SupplierDebtLedger.UnpaidReceipts([receipt], sums.Credit, Today));
    }

    [Fact]
    public void A_payment_covers_the_VAT_too_so_the_phao_receipt_is_paid_at_110000()
    {
        var receipt = new DebtReceiptRow(Guid.Empty, Guid.NewGuid(), "PN000001", Today.AddDays(-1), Total: 100_000, Returned: 0, Vat: 10_000);
        var sums = new SupplierDebtSums(Receipts: 110_000, Returned: 0, Paid: 100_000, PaidBack: 0);

        var unpaid = Assert.Single(SupplierDebtLedger.UnpaidReceipts([receipt], sums.Credit, Today));

        Assert.Equal((10_000m, 10_000m), (unpaid.Remaining, sums.Debt));
    }

    [Fact]
    public void The_statement_shows_a_receipt_at_what_the_supplier_invoiced_and_the_debt_after_it_includes_the_VAT()
    {
        var receipt = new DebtReceiptRow(Guid.Empty, Guid.NewGuid(), "PN000001", Today, Total: 100_000, Returned: 0, Discount: 10_000, Vat: 9_000);
        var back = new StatementReturnRow(Guid.NewGuid(), "TH000001", "PN000001", Today, Value: 11_000);

        var entries = SupplierDebtLedger.Statement(0, [receipt], [back], []);

        Assert.Equal([99_000m, -11_000m], entries.Select(e => e.Amount));
        Assert.Equal(88_000m, entries[^1].DebtAfter);
    }

    // ---- the purchases report ----

    [Fact]
    public void The_report_keeps_the_goods_and_the_VAT_apart_and_the_net_is_what_is_owed()
    {
        var phao = Guid.NewGuid();
        var supplier = Guid.NewGuid();
        PurchaseReportDocument Doc(bool isReturn, decimal discount, decimal amount, decimal vat) =>
            new(Guid.NewGuid(), isReturn ? "TH000001" : "PN000001", Today, isReturn, supplier, "Công ty A", isReturn ? null : PurchaseReceiptKind.Supply, discount,
                [new PurchaseReportLine(phao, "VT000001", "Phao", "cái", 1, amount, vat)]);
        var docs = new[] { Doc(false, 10_000, 100_000, 9_000), Doc(true, 0, 50_000, 5_000) };

        var (groups, totals) = PurchaseReportCalculator.Build(docs, PurchaseReportGrouping.Supplier);

        // Goods: 100,000 - 10,000 received and 50,000 returned; VAT: 9,000 received and 5,000 given back.
        Assert.Equal((90_000m, 50_000m, 44_000m), (totals.ReceivedValue, totals.ReturnedValue, totals.NetValue));
        Assert.Equal((4_000m, 9_000m, 5_000m, 40_000m), (totals.VatAmount, totals.ReceivedVat, totals.ReturnedVat, totals.GoodsValue));
        var group = Assert.Single(groups);
        Assert.Equal((44_000m, 4_000m, 40_000m), (group.NetValue, group.VatAmount, group.GoodsValue));
    }

    [Fact]
    public void Grouped_by_item_the_groups_still_add_up_to_the_totals_and_the_documents_of_a_group_carry_their_VAT()
    {
        var (phao, rope, supplier) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var receipt = new PurchaseReportDocument(
            Guid.NewGuid(), "PN000001", Today, false, supplier, "Công ty A", PurchaseReceiptKind.Supply, 10_000,
            [new PurchaseReportLine(phao, "VT000001", "Phao", "cái", 1, 60_000, 5_400), new PurchaseReportLine(rope, "VT000002", "Dây", "m", 4, 40_000, 0)]);

        var (groups, totals) = PurchaseReportCalculator.Build([receipt], PurchaseReportGrouping.Item);

        Assert.Equal((90_000m, 5_400m, 95_400m), (totals.ReceivedValue, totals.VatAmount, totals.NetValue));
        Assert.Equal(totals.ReceivedValue, groups.Sum(g => g.ReceivedValue));
        Assert.Equal(totals.NetValue, groups.Sum(g => g.NetValue));
        Assert.Equal(totals.VatAmount, groups.Sum(g => g.VatAmount));
        Assert.Equal((60_000m, 5_400m), (groups.Single(g => g.Key == phao.ToString()).ReceivedValue, groups.Single(g => g.Key == phao.ToString()).VatAmount));
        var document = Assert.Single(PurchaseReportCalculator.DocumentsOf([receipt], PurchaseReportGrouping.Item, phao.ToString()));
        Assert.Equal((60_000m, 5_400m), (document.Value, document.VatAmount));
    }
}
