using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Harvest;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Application.Features.Sales.Products;
using OMTS.Application.Features.Sales.Returns;
using OMTS.Application.UnitTests.Features.Purchasing;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>
/// What a sales return does to the product ledger of the lots: Return rows for the lines taken back for sale only, undone when the return is cancelled, and written after the reopen of a closed lot has
/// undone its LotClose rows, so the lot ends holding the kg put back (plus what its close had taken off).
/// </summary>
public sealed class SalesReturnLedgerTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 17, 9, 0, 0, TimeSpan.FromHours(7));
    private static readonly DateOnly Day = new(2026, 11, 17);
    private readonly InMemoryProductStock stock = new();
    private readonly Guid milky = Guid.NewGuid();
    private readonly Guid fat = Guid.NewGuid();

    private sealed class Catalog : IStatusCatalog
    {
        public string CodeOf(int statusId) => $"S{statusId}";

        public string DisplayNameOf(int statusId) => $"Status {statusId}";
    }

    private ProductStockLedger Ledger() => new(stock, new Fakes.FakeCurrentUser(Guid.NewGuid()), new Fakes.FixedTimeProvider(At));

    private IUnitOfWork UnitOfWork() => StubProxy.Create<IUnitOfWork>(h => h["MarkChanged"] = _ => null);

    private HarvestLotStatusService Statuses() =>
        new(new Catalog(), new AuditLogService(new Fakes.FakeAuditEventRepository(), new Fakes.FakeCurrentUser(Guid.NewGuid()), new Catalog(), new Fakes.FixedTimeProvider(At)), Ledger(), UnitOfWork());

    private SalesReturnLots Lots(params HarvestLot[] lots) => new(
        StubProxy.Create<IHarvestLotRepository>(h => h["ListForInvoiceAsync"] = _ => lots.ToList()),
        StubProxy.Create<IHarvestLotSalesQuery>(_ => { }),
        Statuses(),
        null!,
        UnitOfWork(),
        new Fakes.FixedTimeProvider(At));

    private HarvestLot Lot(int statusId = StatusIds.HarvestLot.Open) => new()
    {
        LotCode = "KV01-B01-20261117",
        HarvestedAt = At,
        HarvestDate = Day,
        StatusId = statusId,
        Raft = new Raft { Code = "B01", ZoneId = Guid.NewGuid() },
        ProductLines =
        [
            new HarvestLotProductLine { LineNumber = 1, ProductId = milky, CageCount = 4, Kg = 100m, WeighedKg = 100m },
            new HarvestLotProductLine { LineNumber = 2, ProductId = fat, CageCount = 2, Kg = 50m, WeighedKg = 50m },
        ],
    };

    private static SalesInvoice Invoice(HarvestLot lot, params (Guid ProductId, decimal Kg, decimal Quantity, decimal UnitPrice, decimal Amount)[] lines)
    {
        var invoice = new SalesInvoice { Code = "HD000007", InvoicedAt = At, Customer = new Customer { Code = "KH000009", Name = "Chi Hoa" } };
        var number = 0;
        foreach (var (productId, kg, quantity, unitPrice, amount) in lines)
        {
            invoice.Lines.Add(new SalesInvoiceLine { LineNumber = ++number, ProductId = productId, HarvestLot = lot, HarvestLotId = lot.Id, Kg = kg, Quantity = quantity, UnitPrice = unitPrice, Amount = amount });
        }

        return invoice;
    }

    private static SalesInvoice KgInvoice(HarvestLot lot, params (Guid ProductId, decimal Kg)[] lines) =>
        Invoice(lot, lines.Select(l => (l.ProductId, l.Kg, l.Kg, 60_000m, l.Kg * 60_000m)).ToArray());

    private static SalesReturn ReturnOf(SalesInvoice invoice, params SalesReturnOutcome[] outcomes)
    {
        var salesReturn = new SalesReturn { Code = "TL000001", InvoiceId = invoice.Id, Invoice = invoice };
        var lines = invoice.Lines.OrderBy(l => l.LineNumber).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            salesReturn.Lines.Add(new SalesReturnLine
            {
                InvoiceLineId = lines[i].Id, HarvestLotId = lines[i].HarvestLotId, HarvestLot = lines[i].HarvestLot, ReturnedKg = lines[i].Kg, Outcome = outcomes[i],
                DiscardedKg = SalesReturnRules.DiscardedKg(outcomes[i], lines[i].Kg),
            });
        }

        return salesReturn;
    }

    private decimal OnHand(Guid productId, HarvestLot lot) => stock.Rows.Where(r => r.ProductId == productId && r.HarvestLotId == lot.Id).Sum(r => r.QuantityKg);

    private static Dictionary<Guid, SalesReturnOutcome> Outcomes(SalesInvoice invoice, SalesReturnOutcome outcome) => invoice.Lines.ToDictionary(l => l.Id, _ => outcome);

    [Fact]
    public void A_return_writes_Return_rows_for_the_lines_taken_back_for_sale_only_at_the_price_of_the_line_with_the_customer()
    {
        var lot = Lot();
        // The first line is counted in kg and the second per cage; only the first is taken back for sale.
        var invoice = Invoice(lot, (milky, 40m, 40m, 60_000m, 2_400_000m), (fat, 10m, 2m, 150_000m, 300_000m));
        var salesReturn = ReturnOf(invoice, SalesReturnOutcome.BackForSale, SalesReturnOutcome.ThrownAway);

        Ledger().PostReturn(salesReturn, invoice, invoice.Customer, new Dictionary<Guid, HarvestLot> { [lot.Id] = lot });

        var row = Assert.Single(stock.Rows);
        Assert.Equal((ProductStockMovementKind.Return, milky, lot.Id, 40m, 60_000m), (row.Kind, row.ProductId, row.HarvestLotId, row.QuantityKg, row.TransactionPrice));
        Assert.Equal((salesReturn.Id, "TL000001", "Chi Hoa", "KH000009"), (row.DocumentId, row.DocumentCode, row.PartyName, row.PartyCode));
        Assert.Equal(lot.LotCode, row.LotCode);
    }

    [Fact]
    public void A_line_counted_per_cage_comes_back_at_the_amount_over_its_kg()
    {
        var lot = Lot();
        var invoice = Invoice(lot, (fat, 20m, 2m, 500_000m, 1_000_000m));

        Ledger().PostReturn(ReturnOf(invoice, SalesReturnOutcome.BackForSale), invoice, null, new Dictionary<Guid, HarvestLot> { [lot.Id] = lot });

        Assert.Equal((20m, 50_000m), (stock.Rows.Single().QuantityKg, stock.Rows.Single().TransactionPrice));
    }

    [Fact]
    public void A_line_of_a_lot_not_split_into_its_product_and_a_return_of_thrown_away_lines_write_nothing()
    {
        var lot = Lot();
        var otherProduct = Invoice(lot, (Guid.NewGuid(), 5m, 5m, 60_000m, 300_000m));
        var thrownAway = KgInvoice(lot, (milky, 40m));
        var lots = new Dictionary<Guid, HarvestLot> { [lot.Id] = lot };

        Ledger().PostReturn(ReturnOf(otherProduct, SalesReturnOutcome.BackForSale), otherProduct, null, lots);
        Ledger().PostReturn(ReturnOf(thrownAway, SalesReturnOutcome.ThrownAway), thrownAway, null, lots);

        Assert.Empty(stock.Rows);
    }

    [Fact]
    public async Task Cancelling_the_return_undoes_its_Return_rows_only_and_only_once()
    {
        var lot = Lot();
        var ledger = Ledger();
        ledger.PostHarvest(lot, "B01", [(milky, 100m), (fat, 50m)]);
        var invoice = KgInvoice(lot, (milky, 40m), (fat, 10m));
        ledger.PostSale(invoice, invoice.Customer);
        var salesReturn = ReturnOf(invoice, SalesReturnOutcome.BackForSale, SalesReturnOutcome.BackForSale);
        ledger.PostReturn(salesReturn, invoice, invoice.Customer, new Dictionary<Guid, HarvestLot> { [lot.Id] = lot });
        Assert.Equal((100m, 50m), (OnHand(milky, lot), OnHand(fat, lot)));

        await ledger.ReverseReturnAsync(salesReturn, CancellationToken.None);
        await ledger.ReverseReturnAsync(salesReturn, CancellationToken.None);

        Assert.Equal((60m, 40m), (OnHand(milky, lot), OnHand(fat, lot)));
        Assert.Equal(2, stock.Rows.Count(r => r.Kind == ProductStockMovementKind.Reversal));
        Assert.All(stock.Rows.Where(r => r.Kind == ProductStockMovementKind.Reversal), r => Assert.Equal(salesReturn.Id, r.DocumentId));
        Assert.Equal(2, stock.Rows.Count(r => r.Kind == ProductStockMovementKind.Sale && !stock.Rows.Any(x => x.ReversesMovementId == r.Id)));
    }

    [Fact]
    public async Task A_lot_sold_whole_and_closed_by_its_invoice_holds_the_kg_put_back_once_the_return_has_reopened_it()
    {
        var lot = Lot();
        var ledger = Ledger();
        ledger.PostHarvest(lot, "B01", [(milky, 100m), (fat, 50m)]);
        var invoice = KgInvoice(lot, (milky, 100m), (fat, 50m));
        ledger.PostSale(invoice, invoice.Customer);
        Assert.Null(await Statuses().CloseSoldWholeLotAsync(lot, Day, invoice.Id));
        Assert.Equal((0m, 0m), (OnHand(milky, lot), OnHand(fat, lot)));
        Assert.Empty(stock.Rows.Where(r => r.Kind == ProductStockMovementKind.LotClose));

        var changes = await Lots(lot).ApplyAsync(Outcomes(invoice, SalesReturnOutcome.BackForSale), invoice, null, CancellationToken.None);
        Ledger().PostReturn(ReturnOf(invoice, SalesReturnOutcome.BackForSale, SalesReturnOutcome.BackForSale), invoice, invoice.Customer, changes.Value!.Lots);

        Assert.Equal([lot.LotCode], changes.Value.Reopened);
        Assert.Equal(StatusIds.HarvestLot.Reopened, lot.StatusId);
        Assert.Equal((100m, 50m), (OnHand(milky, lot), OnHand(fat, lot)));
    }

    [Fact]
    public async Task A_lot_closed_with_a_leftover_gets_the_leftover_back_by_the_reopen_and_the_returned_kg_on_top()
    {
        var lot = Lot();
        var ledger = Ledger();
        ledger.PostHarvest(lot, "B01", [(milky, 100m), (fat, 50m)]);
        var invoice = KgInvoice(lot, (milky, 98m), (fat, 50m));
        ledger.PostSale(invoice, invoice.Customer);
        Assert.Null(await Statuses().CloseAsync(lot, Day, 2m, HarvestLotCloseReason.Unsold));
        Assert.Equal((0m, 0m), (OnHand(milky, lot), OnHand(fat, lot)));

        var changes = await Lots(lot).ApplyAsync(Outcomes(invoice, SalesReturnOutcome.BackForSale), invoice, null, CancellationToken.None);
        Ledger().PostReturn(ReturnOf(invoice, SalesReturnOutcome.BackForSale, SalesReturnOutcome.BackForSale), invoice, invoice.Customer, changes.Value!.Lots);

        // 2 kg of the close come back (reopen), 98 kg come back for sale (return): the order of the two does not touch the other's rows.
        Assert.Equal((100m, 50m), (OnHand(milky, lot), OnHand(fat, lot)));
        var close = Assert.Single(stock.Rows, r => r.Kind == ProductStockMovementKind.LotClose);
        Assert.Contains(stock.Rows, r => r.Kind == ProductStockMovementKind.Reversal && r.ReversesMovementId == close.Id);
        Assert.DoesNotContain(stock.Rows, r => r.Kind == ProductStockMovementKind.Reversal && stock.Rows.Single(x => x.Id == r.ReversesMovementId).Kind == ProductStockMovementKind.Return);
    }

    [Fact]
    public async Task A_lot_that_is_still_open_only_gets_the_Return_rows()
    {
        var lot = Lot();
        var ledger = Ledger();
        ledger.PostHarvest(lot, "B01", [(milky, 100m), (fat, 50m)]);
        var invoice = KgInvoice(lot, (milky, 40m), (fat, 10m));
        ledger.PostSale(invoice, invoice.Customer);

        var changes = await Lots(lot).ApplyAsync(Outcomes(invoice, SalesReturnOutcome.BackForSale), invoice, null, CancellationToken.None);
        Ledger().PostReturn(ReturnOf(invoice, SalesReturnOutcome.BackForSale, SalesReturnOutcome.BackForSale), invoice, invoice.Customer, changes.Value!.Lots);

        Assert.Empty(changes.Value.Reopened);
        Assert.Equal((100m, 50m), (OnHand(milky, lot), OnHand(fat, lot)));
        Assert.DoesNotContain(stock.Rows, r => r.Kind is ProductStockMovementKind.Reversal or ProductStockMovementKind.LotClose);
    }

    [Fact]
    public async Task Cancelling_a_return_whose_lot_was_closed_again_writes_the_close_again_so_the_closed_lot_still_holds_nothing()
    {
        var lot = Lot();
        var ledger = Ledger();
        ledger.PostHarvest(lot, "B01", [(milky, 100m), (fat, 50m)]);
        var invoice = KgInvoice(lot, (milky, 100m), (fat, 50m));
        ledger.PostSale(invoice, invoice.Customer);
        await Statuses().CloseSoldWholeLotAsync(lot, Day, invoice.Id);
        var changes = await Lots(lot).ApplyAsync(Outcomes(invoice, SalesReturnOutcome.BackForSale), invoice, null, CancellationToken.None);
        var salesReturn = ReturnOf(invoice, SalesReturnOutcome.BackForSale, SalesReturnOutcome.BackForSale);
        Ledger().PostReturn(salesReturn, invoice, invoice.Customer, changes.Value!.Lots);
        // The kg came back and then stayed unsold: the lot is closed again with all of it taken off.
        Assert.Null(await Statuses().CloseAsync(lot, Day, 150m, HarvestLotCloseReason.Unsold));
        Assert.Equal((0m, 0m), (OnHand(milky, lot), OnHand(fat, lot)));

        await Ledger().ReverseReturnAsync(salesReturn, CancellationToken.None);

        Assert.Equal(StatusIds.HarvestLot.Closed, lot.StatusId);
        Assert.Equal((0m, 0m), (OnHand(milky, lot), OnHand(fat, lot)));
    }

    [Fact]
    public async Task Cancelling_a_return_whose_lot_is_for_sale_takes_the_kg_off_and_may_leave_the_product_below_zero()
    {
        var lot = Lot();
        var ledger = Ledger();
        ledger.PostHarvest(lot, "B01", [(milky, 100m)]);
        var invoice = KgInvoice(lot, (milky, 100m));
        ledger.PostSale(invoice, invoice.Customer);
        var salesReturn = ReturnOf(invoice, SalesReturnOutcome.BackForSale);
        ledger.PostReturn(salesReturn, invoice, invoice.Customer, new Dictionary<Guid, HarvestLot> { [lot.Id] = lot });
        // The oysters were sold again after they came back.
        var second = KgInvoice(lot, (milky, 100m));
        ledger.PostSale(second, second.Customer);

        await ledger.ReverseReturnAsync(salesReturn, CancellationToken.None);

        Assert.Equal(-100m, OnHand(milky, lot));
        Assert.DoesNotContain(stock.Rows, r => r.Kind == ProductStockMovementKind.LotClose);
    }

    [Fact]
    public void An_invoice_has_a_return_while_one_that_is_not_cancelled_stands_and_then_offers_no_new_return()
    {
        var invoice = new SalesInvoice { StatusId = StatusIds.SalesInvoice.Completed };
        invoice.Returns.Add(new SalesReturn { StatusId = StatusIds.SalesReturn.Cancelled });
        Assert.False(SalesInvoiceAllowedActions.HasSalesReturn(invoice));
        Assert.Null(SalesInvoiceAllowedActions.CreateReturnRefusal(invoice.StatusId, SalesInvoiceAllowedActions.HasSalesReturn(invoice), mayCreateReturn: true));

        invoice.Returns.Add(new SalesReturn { StatusId = StatusIds.SalesReturn.Recorded });
        Assert.True(SalesInvoiceAllowedActions.HasSalesReturn(invoice));
        Assert.Equal(SalesReturnRefusal.HasSalesReturn, SalesInvoiceAllowedActions.CreateReturnRefusal(invoice.StatusId, SalesInvoiceAllowedActions.HasSalesReturn(invoice), mayCreateReturn: true));

        var rights = new SalesInvoiceCallerRights(IsCooperativeOwner: true, MaySell: true, MayCreateReturn: true, MayPrint: true);
        var actions = SalesInvoiceAllowedActions.Of(new SalesInvoiceActionFacts(invoice.StatusId, StatusIds.MessageDelivery.Sent, SaleFinished: true, HasSalesReturn: true), rights);
        Assert.DoesNotContain(SalesInvoiceAction.CreateReturn, actions);
        Assert.Contains(SalesInvoiceAction.CancelReturn, actions);
    }
}
