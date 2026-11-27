using System.Text.Json;
using System.Text.Json.Serialization;
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

    private static PurchaseReceipt Receipt(decimal discount, params (decimal Amount, VatRate? Rate)[] lines)
    {
        var receipt = new PurchaseReceipt { Discount = discount, StatusId = StatusIds.PurchaseReceipt.Completed };
        var number = 0;
        foreach (var (amount, rate) in lines)
        {
            receipt.Lines.Add(new PurchaseReceiptLine { LineNumber = ++number, LineAmount = amount, VatRate = rate });
        }

        PurchaseMoney.ApplyVat(receipt);
        return receipt;
    }

    // ---- rates ----

    [Theory]
    [InlineData(VatRate.Kct, "KCT", 0)]
    [InlineData(VatRate.Zero, "0", 0)]
    [InlineData(VatRate.Five, "5", 5)]
    [InlineData(VatRate.Eight, "8", 8)]
    [InlineData(VatRate.Ten, "10", 10)]
    public void A_rate_has_its_text_and_its_percent(VatRate rate, string text, decimal percent)
    {
        Assert.Equal(text, VatRates.Text(rate));
        Assert.Equal(percent, VatRates.Percent(rate));
        Assert.True(VatRates.TryParse(text, out var parsed));
        Assert.Equal(rate, parsed);
    }

    [Theory]
    [InlineData(" 10 ", VatRate.Ten)]
    [InlineData("8%", VatRate.Eight)]
    [InlineData("kct", VatRate.Kct)]
    public void A_rate_is_read_with_a_percent_sign_spaces_and_any_letter_case(string text, VatRate expected)
    {
        Assert.True(VatRates.TryParse(text, out var rate));
        Assert.Equal(expected, rate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12")]
    [InlineData("Ten")]
    [InlineData("-5")]
    public void Anything_else_is_not_a_rate(string? text) => Assert.False(VatRates.TryParse(text, out _));

    [Fact]
    public void No_rate_is_no_VAT_and_seed_is_always_KCT_while_supplies_start_at_ten_percent()
    {
        Assert.Equal(0m, VatRates.Percent(null));
        Assert.Equal(VatRate.Kct, VatRates.DefaultFor(InputItemGroup.Seed));
        Assert.Equal(VatRate.Ten, VatRates.DefaultFor(InputItemGroup.Material));
        Assert.Equal(VatRate.Ten, VatRates.DefaultFor(InputItemGroup.Other));
    }

    [Fact]
    public void A_rate_travels_as_its_text_even_behind_the_string_enum_converter_of_the_API()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new VatRateJsonConverter(), new JsonStringEnumConverter() } };

        Assert.Equal("""{"rate":"10","other":null,"kind":"Seed"}""", JsonSerializer.Serialize(new Wire(VatRate.Ten, null, InputItemGroup.Seed), options));
        Assert.Equal("""{"rate":"KCT","other":"0","kind":"Other"}""", JsonSerializer.Serialize(new Wire(VatRate.Kct, VatRate.Zero, InputItemGroup.Other), options));

        var read = JsonSerializer.Deserialize<Wire>("""{"rate":"8","other":"5","kind":"Material"}""", options)!;
        Assert.Equal((VatRate.Eight, (VatRate?)VatRate.Five, InputItemGroup.Material), (read.Rate, read.Other, read.Kind));
        Assert.Null(JsonSerializer.Deserialize<Wire>("""{"rate":"10","other":null,"kind":"Seed"}""", options)!.Other);
    }

    [Theory]
    [InlineData("\"12\"")]
    [InlineData("10")]
    [InlineData("\"Ten\"")]
    public void A_rate_that_is_not_one_of_the_five_texts_cannot_be_read(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Wire>($$"""{"rate":{{json}},"kind":"Seed"}""", new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new VatRateJsonConverter() } }));

    private sealed record Wire(VatRate Rate, VatRate? Other, InputItemGroup Kind);

    // ---- the VAT of a line ----

    [Theory]
    [InlineData(100_000, 0, VatRate.Ten, 10_000)]
    [InlineData(100_000, 10_000, VatRate.Ten, 9_000)]
    [InlineData(100_000, 0, VatRate.Eight, 8_000)]
    [InlineData(100_000, 0, VatRate.Five, 5_000)]
    [InlineData(100_000, 0, VatRate.Zero, 0)]
    [InlineData(100_000, 0, VatRate.Kct, 0)]
    [InlineData(100_000, 0, null, 0)]
    public void The_VAT_of_a_line_is_its_amount_less_its_share_of_the_receipt_discount_times_the_rate(decimal amount, decimal discount, VatRate? rate, decimal expected) =>
        Assert.Equal(expected, PurchaseMoney.LineVat(amount, amount, discount, rate));

    [Theory]
    [InlineData(5, 0.5, 1)]
    [InlineData(15, 1.5, 2)]
    [InlineData(14, 1.4, 1)]
    [InlineData(4, 0.4, 0)]
    public void The_VAT_is_rounded_half_up_to_the_dong(decimal amount, decimal exactVat, decimal expected)
    {
        Assert.Equal(exactVat, amount * 0.10m);
        Assert.Equal(expected, PurchaseMoney.LineVat(amount, amount, 0, VatRate.Ten));
    }

    [Fact]
    public void The_discount_of_the_receipt_is_shared_by_line_amount_and_every_line_has_its_own_rate()
    {
        // 60,000 at 10% and 40,000 at KCT, 10,000 off: the first line carries 54,000 × 10% = 5,400, the second none.
        var receipt = Receipt(10_000, (60_000m, VatRate.Ten), (40_000m, VatRate.Kct));

        Assert.Equal([5_400m, 0m], receipt.Lines.OrderBy(l => l.LineNumber).Select(l => l.VatAmount));
        var figures = PurchaseMoney.Of(receipt);
        Assert.Equal((100_000m, 5_400m, 95_400m), (figures.Total, figures.Vat, figures.Payable));
    }

    [Fact]
    public void The_share_of_the_discount_is_exact_before_rounding_so_each_line_rounds_once()
    {
        // 3 lines of 33,333 with 10 off: each 33,333 × 99,989 / 99,999 × 8% = 2,666.4 -> 2,666 (not rounded twice).
        var receipt = Receipt(10, (33_333m, VatRate.Eight), (33_333m, VatRate.Eight), (33_333m, VatRate.Eight));

        Assert.All(receipt.Lines, l => Assert.Equal(PurchaseMoney.RoundHalfUp(33_333m * 99_989m * 8m / (99_999m * 100m)), l.VatAmount));
        Assert.Equal(7_998m, PurchaseMoney.Vat(receipt.Lines));
    }

    [Fact]
    public void A_receipt_of_one_phao_at_100000_and_ten_percent_is_owed_110000_and_a_discount_of_10000_leaves_99000()
    {
        var plain = Receipt(0, (100_000m, VatRate.Ten));
        Assert.Equal((10_000m, 110_000m), (PurchaseMoney.Vat(plain.Lines), PurchaseMoney.Of(plain).Payable));

        var discounted = Receipt(10_000, (100_000m, VatRate.Ten));
        Assert.Equal((9_000m, 99_000m), (PurchaseMoney.Vat(discounted.Lines), PurchaseMoney.Of(discounted).Payable));
    }

    [Fact]
    public void A_line_made_before_input_VAT_has_no_rate_and_no_VAT_so_the_figures_stay()
    {
        var old = Receipt(500, (10_000m, null), (2_000m, null));

        var figures = PurchaseMoney.Of(old);

        Assert.Equal((12_000m, 0m, 11_500m), (figures.Total, figures.Vat, figures.Payable));
        Assert.All(old.Lines, l => Assert.Null(l.VatRate));
    }

    [Fact]
    public void Applying_the_VAT_again_after_the_discount_or_a_rate_changes_gives_the_new_figures()
    {
        var receipt = Receipt(0, (100_000m, VatRate.Ten));

        receipt.Discount = 10_000;
        receipt.Lines.Single().VatRate = VatRate.Eight;
        PurchaseMoney.ApplyVat(receipt);

        Assert.Equal(7_200m, receipt.Lines.Single().VatAmount);
    }

    [Fact]
    public void A_receipt_with_no_amount_yet_has_no_VAT()
    {
        var draft = Receipt(0, (0m, VatRate.Ten));
        Assert.Equal(0m, draft.Lines.Single().VatAmount);
        Assert.Equal(0m, PurchaseMoney.LineVat(100m, 0m, 0m, VatRate.Ten));
    }

    // ---- which rate a line gets ----

    [Fact]
    public void A_line_takes_the_rate_sent_else_the_rate_of_its_item_and_a_seed_line_is_always_KCT()
    {
        var phao = new FarmingInputItem { Group = InputItemGroup.Material, PurchaseVatRate = VatRate.Ten };
        var seed = new FarmingInputItem { Group = InputItemGroup.Seed, PurchaseVatRate = VatRate.Kct };

        Assert.Equal(VatRate.Ten, PurchaseReceiptService.LineVatRate(null, phao, kept: null));
        Assert.Equal(VatRate.Eight, PurchaseReceiptService.LineVatRate(VatRate.Eight, phao, kept: null));
        Assert.Equal(VatRate.Kct, PurchaseReceiptService.LineVatRate(VatRate.Ten, seed, kept: null));
        Assert.Equal(VatRate.Kct, PurchaseReceiptService.LineVatRate(null, seed, kept: null));
    }

    [Fact]
    public void An_edit_keeps_the_rate_of_a_kept_line_of_the_same_item_so_an_old_receipt_is_not_taxed_by_an_edit()
    {
        var phao = new FarmingInputItem { Group = InputItemGroup.Material, PurchaseVatRate = VatRate.Ten };
        var other = new FarmingInputItem { Group = InputItemGroup.Other, PurchaseVatRate = VatRate.Five };
        var old = new PurchaseReceiptLine { ItemId = phao.Id, VatRate = null };
        var eight = new PurchaseReceiptLine { ItemId = phao.Id, VatRate = VatRate.Eight };

        Assert.Null(PurchaseReceiptService.LineVatRate(null, phao, old));
        Assert.Equal(VatRate.Eight, PurchaseReceiptService.LineVatRate(null, phao, eight));
        Assert.Equal(VatRate.Ten, PurchaseReceiptService.LineVatRate(VatRate.Ten, phao, old));
        // Another item on the line takes that item's rate.
        Assert.Equal(VatRate.Five, PurchaseReceiptService.LineVatRate(null, other, eight));
    }

    [Fact]
    public void A_line_sent_again_without_a_rate_keeps_the_stored_rate_and_only_a_new_line_takes_the_item_rate()
    {
        var phao = new FarmingInputItem { Group = InputItemGroup.Material, PurchaseVatRate = VatRate.Ten };
        var stored = new PurchaseReceiptLine { ItemId = phao.Id, VatRate = null };

        // Existing line, same item, no rate sent: none stays none, so totals and debt of an old receipt do not move.
        Assert.Null(PurchaseReceiptService.LineVatRate(null, phao, stored));
        // New line (nothing stored for it): the item's rate.
        Assert.Equal(VatRate.Ten, PurchaseReceiptService.LineVatRate(null, phao, new PurchaseReceiptLine()));

        // The re-saved old receipt keeps its figures: the lines take the stored rate and the VAT is worked out again to 0.
        var receipt = Receipt(0, (100_000m, null));
        receipt.Lines.Single().ItemId = phao.Id;
        receipt.Lines.Single().VatRate = PurchaseReceiptService.LineVatRate(null, phao, receipt.Lines.Single());
        PurchaseMoney.ApplyVat(receipt);
        Assert.Equal((0m, 100_000m), (PurchaseMoney.Vat(receipt.Lines), PurchaseMoney.Of(receipt).Payable));
    }

    // ---- returns ----

    [Fact]
    public void A_return_line_gives_back_the_VAT_of_the_rate_it_holds_on_its_recorded_value()
    {
        var line = new PurchaseReturnLine { VatRate = VatRate.Ten };

        ReceiptLineUnits.FillReturn(line, "cái", null, 1m, 1m, 100_000m);

        Assert.Equal((100_000m, 10_000m), (line.ReturnValue, line.VatAmount));
        var kct = new PurchaseReturnLine { VatRate = VatRate.Kct };
        ReceiptLineUnits.FillReturn(kct, "cái", null, 1m, 1m, 100_000m);
        var old = new PurchaseReturnLine();
        ReceiptLineUnits.FillReturn(old, "cái", null, 1m, 1m, 100_000m);
        Assert.Equal((0m, 0m), (kct.VatAmount, old.VatAmount));
    }

    [Fact]
    public void The_refund_of_a_return_is_its_lines_and_their_VAT_less_its_discount_rounded_half_up_per_line()
    {
        var purchaseReturn = new PurchaseReturn { Discount = 1_000 };
        var rope = new PurchaseReturnLine { VatRate = VatRate.Eight };
        var buoy = new PurchaseReturnLine { VatRate = VatRate.Ten };
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
        var receipt = Receipt(0, (100_000m, VatRate.Ten));
        var line = receipt.Lines.Single();
        var recorded = new PurchaseReturn { StatusId = StatusIds.PurchaseReturn.Recorded };
        var phao = new PurchaseReturnLine { ReceiptLineId = line.Id, VatRate = line.VatRate };
        ReceiptLineUnits.FillReturn(phao, "cái", null, 1m, 1m, 100_000m);
        recorded.Lines.Add(phao);
        receipt.Returns.Add(recorded);
        var cancelled = new PurchaseReturn { StatusId = StatusIds.PurchaseReturn.Cancelled };
        var again = new PurchaseReturnLine { ReceiptLineId = line.Id, VatRate = line.VatRate };
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
        var phao = new PurchaseReceiptLine { VatRate = VatRate.Ten, UnitFactor = 1m, EnteredUnitPrice = 100_000m };
        var discounted = new PurchaseReceiptLine { VatRate = VatRate.Ten, UnitFactor = 1m, EnteredUnitPrice = 100_000m, EnteredDiscount = 5_000m };
        var box = new PurchaseReceiptLine { VatRate = VatRate.Eight, UnitFactor = 12m, EnteredUnitPrice = 120_000m };
        var kct = new PurchaseReceiptLine { VatRate = VatRate.Kct, UnitFactor = 1m, EnteredUnitPrice = 100_000m };
        var old = new PurchaseReceiptLine { UnitFactor = 1m, EnteredUnitPrice = 100_000m };

        Assert.Equal(110_000m, ReceiptLineUnits.CostPerBase(phao));
        Assert.Equal(104_500m, ReceiptLineUnits.CostPerBase(discounted));
        Assert.Equal(10_800m, ReceiptLineUnits.CostPerBase(box));
        Assert.Equal(100_000m, ReceiptLineUnits.CostPerBase(kct));
        Assert.Equal(100_000m, ReceiptLineUnits.CostPerBase(old));
        Assert.Equal(1.1m, ReceiptLineUnits.WithVat(1m, VatRate.Ten));
    }

    private sealed class Account : ICurrentUser
    {
        public Guid? AccountId => null;
        public Guid? SessionId => null;
    }

    private sealed class Fixture(InputCostMethod method, decimal? cost)
    {
        public InMemoryStock Stock { get; } = new();
        public FarmingInputItem Phao { get; } = new() { Code = "VT000001", Name = "Phao", Group = InputItemGroup.Material, Unit = "cái", CostPrice = cost, PurchaseVatRate = VatRate.Ten };

        public InputStockLedger Ledger() => new(
            Stock,
            StubProxy.Create<IFarmingInputItemRepository>(h => h["GetByIdsForUpdateAsync"] = a => new List<FarmingInputItem> { Phao }.Where(i => ((IReadOnlyCollection<Guid>)a[0]!).Contains(i.Id)).ToList()),
            StubProxy.Create<ICooperativeRepository>(h => h["GetCurrentAsync"] = _ => new Cooperative { InputCostMethod = method }),
            StubProxy.Create<ISupplierRepository>(h => h["GetForEditAsync"] = _ => new Supplier { Name = "Công ty A" }),
            new Account(),
            TimeProvider.System);

        public void Stocked(decimal quantity) =>
            Stock.Add(new InputStockMovement { ItemId = Phao.Id, Kind = InputStockMovementKind.Opening, DocumentId = Phao.Id, DocumentCode = Phao.Code, OccurredAt = At, Quantity = quantity });

        public PurchaseReceipt Receipt(decimal quantity, decimal price, VatRate? rate)
        {
            var receipt = new PurchaseReceipt
            {
                Code = "PN000001", ReceivedAt = At,
                Lines = [new PurchaseReceiptLine { LineNumber = 1, ItemId = Phao.Id, Quantity = quantity, EnteredQuantity = quantity, EnteredUnitPrice = price, UnitFactor = 1m, VatRate = rate }],
            };
            return receipt;
        }

        public PurchaseReturn Return(decimal quantity, decimal price, VatRate? rate)
        {
            var line = new PurchaseReturnLine { ItemId = Phao.Id, VatRate = rate };
            ReceiptLineUnits.FillReturn(line, "cái", null, 1m, quantity, price);
            return new PurchaseReturn { Code = "TH000001", ReturnedAt = At, Lines = [line] };
        }
    }

    [Fact]
    public async Task A_receipt_of_the_phao_with_no_stock_sets_the_cost_and_the_stock_card_price_to_110000()
    {
        var f = new Fixture(InputCostMethod.Average, null);

        await f.Ledger().PostReceiptAsync(f.Receipt(1m, 100_000m, VatRate.Ten), CancellationToken.None);

        var row = Assert.Single(f.Stock.Rows, r => r.Kind == InputStockMovementKind.Receipt);
        Assert.Equal((110_000m, 110_000m), (row.TransactionPrice!.Value, f.Phao.CostPrice!.Value));
        Assert.Equal(110_000m, f.Phao.LastPurchasePrice);
    }

    [Fact]
    public async Task The_fixed_cost_of_a_first_receipt_is_the_price_with_VAT()
    {
        var f = new Fixture(InputCostMethod.Fixed, null);

        await f.Ledger().PostReceiptAsync(f.Receipt(2m, 50_000m, VatRate.Eight), CancellationToken.None);

        Assert.Equal(54_000m, f.Phao.CostPrice);
    }

    [Fact]
    public async Task The_average_cost_counts_the_VAT_of_a_receipt_and_of_the_return_that_reverses_it()
    {
        var f = new Fixture(InputCostMethod.Average, 100m);
        f.Stocked(10m);

        // 10 on hand at 100, 10 received at 200 + 10% = 220: (1,000 + 2,200) / 20 = 160.
        await f.Ledger().PostReceiptAsync(f.Receipt(10m, 200m, VatRate.Ten), CancellationToken.None);
        Assert.Equal(160m, f.Phao.CostPrice);

        // 5 go back at 200 + 10% = 220: (20 × 160 − 5 × 220) / 15 = 140.
        await f.Ledger().PostReturnAsync(f.Return(5m, 200m, VatRate.Ten), CancellationToken.None);
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
        var receipt = f.Receipt(1m, 100_000m, VatRate.Ten);
        await f.Ledger().PostReceiptAsync(receipt, CancellationToken.None);
        Assert.Equal(110_000m, f.Phao.CostPrice);

        receipt.Lines.Single().VatRate = VatRate.Eight;
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
