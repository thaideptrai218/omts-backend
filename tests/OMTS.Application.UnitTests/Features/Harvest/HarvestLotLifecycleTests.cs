using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Harvest;
using OMTS.Application.Features.Sales.Orders;
using OMTS.Application.Features.Sales.Products;
using OMTS.Application.UnitTests.Features.Purchasing;
using OMTS.Application.UnitTests.Features.Sales;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Harvest;

/// <summary>
/// The life of a lot on the product ledger and in its figures: the weighing writes the Harvest rows at the weighed kg and weighing again undoes only those, a close takes what is left of each product off the stock
/// (LotClose) and a reopen gives it back, the figures and the days before automatic close count from the weighing, and an invoice cancelled sends its order back to Draft when none other stands.
/// </summary>
public sealed class HarvestLotLifecycleTests
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

    private AuditLogService Audit() => new(new Fakes.FakeAuditEventRepository(), new Fakes.FakeCurrentUser(Guid.NewGuid()), new Catalog(), new Fakes.FixedTimeProvider(At));

    private IUnitOfWork UnitOfWork() => StubProxy.Create<IUnitOfWork>(h => h["MarkChanged"] = _ => null);

    private HarvestLotStatusService Statuses() => new(new Catalog(), Audit(), Ledger(), UnitOfWork());

    private HarvestLotProductLineService Lines() =>
        new(StubProxy.Create<IProductRepository>(_ => { }), StubProxy.Create<IHarvestLotRepository>(_ => { }), StubProxy.Create<IHarvestLotSalesQuery>(_ => { }), stock, Ledger());

    private HarvestLot Lot(int statusId = StatusIds.HarvestLot.AwaitingWeighing) => new()
    {
        LotCode = "KV01-B01-20261117",
        HarvestedAt = At,
        HarvestDate = Day,
        StatusId = statusId,
        Raft = new Raft { Code = "B01", ZoneId = Guid.NewGuid() },
        EstimatedKg = 150m,
        ProductLines =
        [
            new HarvestLotProductLine { LineNumber = 1, ProductId = milky, CageCount = 4, Kg = 100m },
            new HarvestLotProductLine { LineNumber = 2, ProductId = fat, CageCount = 2, Kg = 50m },
        ],
    };

    private static IReadOnlyDictionary<int, decimal> Weighed(decimal first, decimal second) => new Dictionary<int, decimal> { [1] = first, [2] = second };

    private decimal OnHand(Guid productId, HarvestLot lot) => stock.Rows.Where(r => r.ProductId == productId && r.HarvestLotId == lot.Id).Sum(r => r.QuantityKg);

    private void Sell(HarvestLot lot, Guid productId, decimal kg) => stock.Add(new ProductStockMovement
    {
        ProductId = productId, HarvestLotId = lot.Id, LotCode = lot.LotCode, Kind = ProductStockMovementKind.Sale, DocumentId = Guid.NewGuid(), DocumentCode = "HD000001", QuantityKg = -kg,
    });

    [Fact]
    public void A_lot_not_weighed_yet_is_not_for_sale_and_a_weighed_one_is()
    {
        Assert.False(Lot().IsForSale);
        Assert.True(Lot(StatusIds.HarvestLot.Open).IsForSale);
        Assert.True(Lot(StatusIds.HarvestLot.Reopened).IsForSale);
        Assert.False(Lot(StatusIds.HarvestLot.Closed).IsForSale);
    }

    [Fact]
    public async Task Weighing_writes_the_harvest_rows_at_the_weighed_kg_of_each_line()
    {
        var lot = Lot();

        await Lines().WeighAsync(lot, "B01", Weighed(96.5m, 52m), CancellationToken.None);

        Assert.Equal([96.5m, 52m], lot.ProductLines.OrderBy(l => l.LineNumber).Select(l => l.WeighedKg!.Value));
        Assert.All(stock.Rows, r => Assert.Equal(ProductStockMovementKind.Harvest, r.Kind));
        Assert.Equal((96.5m, 52m), (OnHand(milky, lot), OnHand(fat, lot)));
    }

    [Fact]
    public async Task Weighing_again_undoes_only_the_harvest_rows_and_leaves_the_sales_of_the_lot()
    {
        var lot = Lot();
        await Lines().WeighAsync(lot, "B01", Weighed(96.5m, 52m), CancellationToken.None);
        Sell(lot, milky, 30m);
        lot.WeighedKg = 148.5m;

        await Lines().WeighAsync(lot, "B01", Weighed(99m, 50m), CancellationToken.None);

        Assert.Equal(69m, OnHand(milky, lot));
        Assert.Equal(50m, OnHand(fat, lot));
        var sale = Assert.Single(stock.Rows, r => r.Kind == ProductStockMovementKind.Sale);
        Assert.DoesNotContain(stock.Rows, r => r.Kind == ProductStockMovementKind.Reversal && r.ReversesMovementId == sale.Id);
        Assert.Equal(2, stock.Rows.Count(r => r.Kind == ProductStockMovementKind.Reversal));
    }

    [Fact]
    public async Task A_close_takes_what_is_left_of_each_product_off_the_stock_and_skips_a_product_with_none()
    {
        var lot = Lot(StatusIds.HarvestLot.Open);
        await Lines().WeighAsync(lot, "B01", Weighed(100m, 10m), CancellationToken.None);
        Sell(lot, milky, 30m);
        Sell(lot, fat, 10m);

        var refused = await Statuses().CloseAsync(lot, Day, 70m, HarvestLotCloseReason.Unsold);

        Assert.Null(refused);
        Assert.Equal(StatusIds.HarvestLot.Closed, lot.StatusId);
        var close = Assert.Single(stock.Rows, r => r.Kind == ProductStockMovementKind.LotClose);
        Assert.Equal((milky, lot.Id, -70m), (close.ProductId, close.HarvestLotId, close.QuantityKg));
        Assert.Equal((0m, 0m), (OnHand(milky, lot), OnHand(fat, lot)));
    }

    [Fact]
    public async Task A_sold_whole_lot_close_sees_the_sale_rows_of_the_same_request()
    {
        var lot = Lot(StatusIds.HarvestLot.Open);
        await Lines().WeighAsync(lot, "B01", Weighed(100m, 50m), CancellationToken.None);
        Sell(lot, milky, 100m);
        Sell(lot, fat, 20m);

        Assert.Null(await Statuses().CloseSoldWholeLotAsync(lot, Day, Guid.NewGuid()));

        Assert.Equal((0m, 0m), (OnHand(milky, lot), OnHand(fat, lot)));
        Assert.Single(stock.Rows, r => r.Kind == ProductStockMovementKind.LotClose);
    }

    [Fact]
    public async Task A_lot_with_no_product_lines_closes_without_touching_the_ledger()
    {
        var lot = Lot(StatusIds.HarvestLot.Open);
        lot.ProductLines.Clear();

        Assert.Null(await Statuses().CloseAsync(lot, Day, 0m, null));

        Assert.Empty(stock.Rows);
    }

    [Fact]
    public async Task A_reopen_by_hand_gives_back_the_kg_of_the_close_and_leaves_the_other_rows()
    {
        var lot = Lot(StatusIds.HarvestLot.Open);
        await Lines().WeighAsync(lot, "B01", Weighed(100m, 50m), CancellationToken.None);
        Sell(lot, milky, 30m);
        await Statuses().CloseAsync(lot, Day, 120m, HarvestLotCloseReason.Spoiled);
        var statuses = Statuses();

        var reopened = await statuses.ReopenAsync(lot, Day.AddDays(1), HarvestLotReopenCause.ReopenedByHand, reason: "Counted again");

        Assert.True(reopened);
        Assert.Equal((StatusIds.HarvestLot.Reopened, Day.AddDays(1), (HarvestLotCloseReason?)null), (lot.StatusId, lot.ReopenedOn!.Value, lot.CloseReason));
        Assert.Equal((70m, 50m), (OnHand(milky, lot), OnHand(fat, lot)));
        Assert.Single(stock.Rows, r => r.Kind == ProductStockMovementKind.Sale);
        Assert.Equal(2, stock.Rows.Count(r => r.Kind == ProductStockMovementKind.Reversal));
    }

    [Theory]
    [InlineData(HarvestLotCloseReason.Unsold, true)]
    [InlineData(HarvestLotCloseReason.Spoiled, true)]
    [InlineData(HarvestLotCloseReason.TimeLimitReached, true)]
    [InlineData(HarvestLotCloseReason.SoldWholeLot, false)]
    public async Task Only_a_lot_not_closed_by_an_invoice_reopens_by_hand(HarvestLotCloseReason reason, bool mayReopen)
    {
        var lot = Lot(StatusIds.HarvestLot.Closed);
        lot.CloseReason = reason;
        lot.ClosedBySalesInvoiceId = reason == HarvestLotCloseReason.SoldWholeLot ? Guid.NewGuid() : null;

        Assert.Equal(mayReopen, HarvestLotStatusService.MayReopenByHand(lot));
        Assert.Equal(mayReopen, await Statuses().ReopenAsync(lot, Day, HarvestLotReopenCause.ReopenedByHand, reason: "Why"));
        Assert.Equal(mayReopen ? StatusIds.HarvestLot.Reopened : StatusIds.HarvestLot.Closed, lot.StatusId);
    }

    [Fact]
    public async Task A_lot_that_is_not_closed_does_not_reopen_by_hand()
    {
        var lot = Lot(StatusIds.HarvestLot.Open);

        Assert.False(await Statuses().ReopenAsync(lot, Day, HarvestLotReopenCause.ReopenedByHand, reason: "Why"));
        Assert.Equal(StatusIds.HarvestLot.Open, lot.StatusId);
    }

    [Fact]
    public void The_figures_count_from_the_weighed_kg_once_there_is_one()
    {
        var lot = new HarvestLot { EstimatedKg = 500m, StatusId = StatusIds.HarvestLot.Open };
        var sales = new LotSalesFigures(200m, 20m, 20m, 0m);

        var estimated = HarvestLotFigures.Of(lot, sales);
        lot.WeighedKg = 480m;
        var weighed = HarvestLotFigures.Of(lot, sales);

        Assert.Equal((320m, 320m), (estimated.RemainingKg, estimated.DifferenceKg));
        Assert.Equal(300m, weighed.RemainingKg);
        Assert.Equal(300m, weighed.DifferenceKg);
        Assert.Equal(180m, weighed.SoldKg);
        Assert.True(weighed.NotFinal);
    }

    [Fact]
    public void A_closed_lot_works_its_loss_and_difference_from_the_weighed_kg()
    {
        var lot = new HarvestLot { EstimatedKg = 500m, WeighedKg = 480m, LeftoverKg = 40m, StatusId = StatusIds.HarvestLot.Closed };

        var figures = HarvestLotFigures.Of(lot, new LotSalesFigures(440m, 0m, 0m, 0m));

        Assert.Equal((440m, 40m, 0m, false), (figures.SoldKg, figures.LostKg, figures.DifferenceKg, figures.NotFinal));
    }

    [Fact]
    public void The_days_before_automatic_close_count_from_the_reopen_day_else_the_weighing_day_else_the_harvest_date()
    {
        var harvested = new DateOnly(2026, 11, 10);
        var weighedOn = new DateOnly(2026, 11, 12);
        var reopenedOn = new DateOnly(2026, 11, 15);

        Assert.Equal(harvested, HarvestLotAutoCloseRule.ForSaleSince(new HarvestLot { HarvestDate = harvested }));
        Assert.Equal(weighedOn, HarvestLotAutoCloseRule.ForSaleSince(new HarvestLot { HarvestDate = harvested, WeighedOn = weighedOn }));
        Assert.Equal(reopenedOn, HarvestLotAutoCloseRule.ForSaleSince(new HarvestLot { HarvestDate = harvested, WeighedOn = weighedOn, ReopenedOn = reopenedOn }));
        // 3 days from the weighing on 12/11: sold until the end of 15/11, closed by the check of 16/11.
        Assert.Equal(new DateOnly(2026, 11, 16), HarvestLotAutoCloseRule.ClosesOn(weighedOn, 3));
    }

    private SalesOrderProgress Progress() => new(
        StubProxy.Create<ISalesOrderRepository>(_ => { }), Audit(), UnitOfWork(), new Fakes.FakeCurrentUser(Guid.NewGuid()), new Fakes.FixedTimeProvider(At));

    [Theory]
    [InlineData(StatusIds.SalesOrder.Delivering, false, true, StatusIds.SalesOrder.Draft)]
    [InlineData(StatusIds.SalesOrder.Completed, false, true, StatusIds.SalesOrder.Draft)]
    [InlineData(StatusIds.SalesOrder.Completed, true, true, StatusIds.SalesOrder.Delivering)]
    [InlineData(StatusIds.SalesOrder.Delivering, true, false, StatusIds.SalesOrder.Delivering)]
    [InlineData(StatusIds.SalesOrder.Draft, false, false, StatusIds.SalesOrder.Draft)]
    [InlineData(StatusIds.SalesOrder.Cancelled, false, false, StatusIds.SalesOrder.Cancelled)]
    public void A_cancelled_invoice_sends_its_order_to_Draft_when_no_other_invoice_stands_else_to_Delivering(int from, bool otherLiveInvoice, bool moved, int expected)
    {
        var order = new SalesOrder { StatusId = from, CompletedAt = at(from), CompletedManually = from == StatusIds.SalesOrder.Completed };

        var result = Progress().OnInvoiceCancelled(order, otherLiveInvoice);

        Assert.Equal((moved, expected), (result, order.StatusId));
        if (moved)
        {
            Assert.Null(order.CompletedAt);
            Assert.False(order.CompletedManually);
        }

        static DateTimeOffset? at(int status) => status == StatusIds.SalesOrder.Completed ? At : null;
    }
}
