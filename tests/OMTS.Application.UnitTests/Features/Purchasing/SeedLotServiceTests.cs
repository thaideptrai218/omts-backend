using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing.SeedLots;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Purchasing;

/// <summary>The seed lot list: what reaches the database as a filter, the state filter and the counts of its pills, the sums, the order, the page and the detail of one lot.</summary>
public sealed class SeedLotServiceTests
{
    private static readonly DateTimeOffset Received = new(2026, 11, 10, 8, 0, 0, TimeSpan.FromHours(7));
    private readonly Guid supplierId = Guid.NewGuid();
    private readonly Guid itemId = Guid.NewGuid();
    private readonly Guid productId = Guid.NewGuid();

    private sealed class Catalog : IStatusCatalog
    {
        public string CodeOf(int statusId) => $"S{statusId}";

        public string DisplayNameOf(int statusId) => $"Status {statusId}";
    }

    private SeedLotFiguresRow Lot(string receipt, int line, SeedLotFigures figures, DateTimeOffset? receivedAt = null, bool withProduct = true) => new(
        Guid.NewGuid(), line, Guid.NewGuid(), receipt, receivedAt ?? Received, supplierId, "NCC001", "Trại giống", itemId, "GI000001", "Hàu giống 3 cm",
        withProduct ? productId : null, withProduct ? "SP000001" : null, withProduct ? "Hàu béo" : null, "con", figures);

    private static SeedLotFigures Figures(
        decimal received = 100m, decimal returned = 0m, decimal nurseryIn = 0m, decimal nurseryLoss = 0m, decimal viaNursery = 0m, decimal direct = 0m, decimal issued = 0m) =>
        new(received, returned, nurseryIn, nurseryLoss, viaNursery, direct, issued);

    private sealed class FakeQuery(IReadOnlyList<SeedLotFiguresRow> rows) : ISeedLotRegisterQuery
    {
        public SeedLotRegisterFilter? Filter { get; private set; }

        public SeedLotDetailRows? Detail { get; set; }

        public Task<IReadOnlyList<SeedLotFiguresRow>> SearchAsync(SeedLotRegisterFilter filter, CancellationToken cancellationToken)
        {
            Filter = filter;
            return Task.FromResult(rows);
        }

        public Task<SeedLotFiguresRow?> GetAsync(Guid lineId, CancellationToken cancellationToken) => Task.FromResult(rows.FirstOrDefault(r => r.LineId == lineId));

        public Task<IReadOnlyDictionary<Guid, SeedLotFigures>> FiguresAsync(IReadOnlyCollection<Guid> lineIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ILookup<Guid, SeedLotFigures>> FiguresOfReceiptsAsync(IReadOnlyCollection<Guid> receiptIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SeedLotDetailRows> DetailAsync(Guid lineId, CancellationToken cancellationToken) => Task.FromResult(Detail ?? new SeedLotDetailRows([], [], [], [], [], []));
    }

    private static SeedLotService Service(FakeQuery query) => new(query, new Catalog());

    private IReadOnlyList<SeedLotFiguresRow> Rows() =>
    [
        Lot("PN000010", 1, Figures()),
        Lot("PN000010", 2, Figures(nurseryIn: 30m)),
        Lot("PN000011", 1, Figures(direct: 100m)),
        Lot("PN000012", 1, Figures(returned: 100m)),
        Lot("PN000012", 10, Figures(direct: 40m)),
        Lot("PN000012", 2, Figures(direct: 100m)),
    ];

    [Fact]
    public async Task The_filters_reach_the_query_as_asked_with_the_search_trimmed()
    {
        var query = new FakeQuery([]);
        var zone = Guid.NewGuid();

        var result = await Service(query).SearchAsync(
            new SeedLotListQuery(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), supplierId, itemId, zone, null, "  PN0000  "), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(new SeedLotRegisterFilter(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), supplierId, itemId, zone, "PN0000"), query.Filter);
    }

    [Fact]
    public async Task A_blank_search_and_no_period_mean_no_filter()
    {
        var query = new FakeQuery([]);

        await Service(query).SearchAsync(new SeedLotListQuery(Search: "   "), CancellationToken.None);

        Assert.Equal(new SeedLotRegisterFilter(), query.Filter);
    }

    [Fact]
    public async Task A_period_that_ends_before_it_starts_is_refused()
    {
        var result = await Service(new FakeQuery([])).SearchAsync(new SeedLotListQuery(new DateOnly(2026, 11, 30), new DateOnly(2026, 11, 1)), CancellationToken.None);

        Assert.Equal((ErrorKind.Validation, "Period"), (result.Error!.Kind, result.Error.Details!["field"]));
    }

    [Theory]
    [InlineData("Bogus")]
    [InlineData("7")]
    [InlineData("Unused,Bogus")]
    public async Task An_unknown_state_is_refused(string state)
    {
        var result = await Service(new FakeQuery(Rows())).SearchAsync(new SeedLotListQuery(State: state), CancellationToken.None);

        Assert.Equal("State", result.Error!.Details!["field"]);
    }

    [Theory]
    [InlineData("bogus", null, "Sort")]
    [InlineData(null, "sideways", "Sort direction")]
    public async Task An_unknown_sort_or_direction_is_refused(string? sort, string? direction, string field)
    {
        var result = await Service(new FakeQuery(Rows())).SearchAsync(new SeedLotListQuery(Sort: sort, SortDir: direction), CancellationToken.None);

        Assert.Equal(field, result.Error!.Details!["field"]);
    }

    [Fact]
    public async Task The_state_filter_keeps_the_lots_in_those_states_and_the_pill_counts_ignore_it()
    {
        var service = Service(new FakeQuery(Rows()));

        var one = await service.SearchAsync(new SeedLotListQuery(State: "FullySeeded"), CancellationToken.None);
        var several = await service.SearchAsync(new SeedLotListQuery(State: "unused, Nursing"), CancellationToken.None);

        Assert.Equal(2, one.Value!.Total);
        Assert.All(one.Value.Items, r => Assert.Equal(SeedLotState.FullySeeded, r.State));
        Assert.Equal(2, several.Value!.Total);
        Assert.Equal(new[] { SeedLotState.Unused, SeedLotState.Nursing }, several.Value.Items.Select(r => r.State).Order().ToArray());
        // The pills show every state under the other filters, the chosen one or not.
        Assert.Equal(
            (1, 1, 1, 2, 1, 0, 0),
            (one.Value.StateCounts[SeedLotState.Unused], one.Value.StateCounts[SeedLotState.Nursing], one.Value.StateCounts[SeedLotState.PartlySeeded],
                one.Value.StateCounts[SeedLotState.FullySeeded], one.Value.StateCounts[SeedLotState.FullyReturned], one.Value.StateCounts[SeedLotState.FullyLost],
                one.Value.StateCounts[SeedLotState.UsedUp]));
        Assert.Equal(Enum.GetValues<SeedLotState>().Length, one.Value.StateCounts.Count);
    }

    [Fact]
    public async Task The_totals_cover_every_lot_the_filters_match_not_only_the_page()
    {
        var service = Service(new FakeQuery(Rows()));

        var all = await service.SearchAsync(new SeedLotListQuery(PageSize: 2), CancellationToken.None);
        var seeded = await service.SearchAsync(new SeedLotListQuery(State: "FullySeeded", PageSize: 1), CancellationToken.None);

        Assert.Equal((6, 2), (all.Value!.Total, all.Value.Items.Count));
        Assert.Equal((600m, 100m, 30m, 0m, 240m, 0m), (all.Value.Totals.ReceivedQty, all.Value.Totals.ReturnedQty, all.Value.Totals.NursingQty, all.Value.Totals.NurseryLossQty, all.Value.Totals.SeededQty, all.Value.Totals.IssuedOtherQty));
        Assert.Equal((2, 1, 200m, 200m, 0m), (seeded.Value!.Total, seeded.Value.Items.Count, seeded.Value.Totals.ReceivedQty, seeded.Value.Totals.SeededQty, seeded.Value.Totals.RemainingQty));
    }

    [Fact]
    public async Task The_default_order_is_the_lot_code_newest_first_and_a_line_ten_comes_after_line_two()
    {
        var service = Service(new FakeQuery(Rows()));

        var newest = await service.SearchAsync(new SeedLotListQuery(), CancellationToken.None);
        var oldest = await service.SearchAsync(new SeedLotListQuery(SortDir: "asc"), CancellationToken.None);

        Assert.Equal(["PN000012-10", "PN000012-2", "PN000012-1", "PN000011-1", "PN000010-2", "PN000010-1"], newest.Value!.Items.Select(r => r.SeedLotCode).ToArray());
        Assert.Equal(["PN000010-1", "PN000010-2", "PN000011-1", "PN000012-1", "PN000012-2", "PN000012-10"], oldest.Value!.Items.Select(r => r.SeedLotCode).ToArray());
    }

    [Fact]
    public async Task The_list_can_be_sorted_by_the_date_received_or_by_the_quantity_remaining_and_pages_do_not_repeat_a_lot()
    {
        var rows = new[]
        {
            Lot("PN000001", 1, Figures(direct: 90m), Received.AddDays(2)),
            Lot("PN000002", 1, Figures(direct: 10m), Received),
            Lot("PN000003", 1, Figures(direct: 50m), Received.AddDays(1)),
            Lot("PN000004", 1, Figures(direct: 50m), Received.AddDays(1)),
        };
        var service = Service(new FakeQuery(rows));

        var byDate = await service.SearchAsync(new SeedLotListQuery(Sort: "receivedAt", SortDir: "asc"), CancellationToken.None);
        var byRemaining = await service.SearchAsync(new SeedLotListQuery(Sort: "remaining"), CancellationToken.None);
        var first = await service.SearchAsync(new SeedLotListQuery(Sort: "remaining", PageSize: 2, Page: 1), CancellationToken.None);
        var second = await service.SearchAsync(new SeedLotListQuery(Sort: "remaining", PageSize: 2, Page: 2), CancellationToken.None);

        // Oldest first; the two lots received on the same day by code, oldest code first.
        Assert.Equal(["PN000002-1", "PN000003-1", "PN000004-1", "PN000001-1"], byDate.Value!.Items.Select(r => r.SeedLotCode).ToArray());
        // Most remaining first: 90 (lot 2), then the two lots of 50 by code, newest code first, then 10.
        Assert.Equal(["PN000002-1", "PN000004-1", "PN000003-1", "PN000001-1"], byRemaining.Value!.Items.Select(r => r.SeedLotCode).ToArray());
        Assert.Equal(["PN000002-1", "PN000004-1"], first.Value!.Items.Select(r => r.SeedLotCode).ToArray());
        Assert.Equal(["PN000003-1", "PN000001-1"], second.Value!.Items.Select(r => r.SeedLotCode).ToArray());
    }

    [Fact]
    public async Task A_page_size_above_the_limit_is_capped_and_a_page_below_one_reads_as_the_first()
    {
        var service = Service(new FakeQuery(Rows()));

        var result = await service.SearchAsync(new SeedLotListQuery(Page: 0, PageSize: 5_000), CancellationToken.None);

        Assert.Equal((1, SeedLotService.MaxPageSize, 6), (result.Value!.Page, result.Value.PageSize, result.Value.Items.Count));
    }

    [Fact]
    public async Task A_row_carries_the_references_the_quantities_and_the_state_with_the_product_when_the_seed_has_one()
    {
        var withProduct = Lot("PN000010", 1, Figures(returned: 10m, nurseryIn: 30m, nurseryLoss: 5m, viaNursery: 10m, direct: 20m, issued: 5m));
        var withoutProduct = Lot("PN000011", 1, Figures(), withProduct: false);
        var service = Service(new FakeQuery([withProduct, withoutProduct]));

        var page = (await service.SearchAsync(new SeedLotListQuery(Sort: "code", SortDir: "asc"), CancellationToken.None)).Value!;

        var row = page.Items[0];
        Assert.Equal(withProduct.LineId, row.Id);
        Assert.Equal(("PN000010-1", withProduct.ReceiptId, "PN000010"), (row.SeedLotCode, row.ReceiptId, row.ReceiptCode));
        Assert.Equal((supplierId, "NCC001", "Trại giống"), (row.Supplier.Id, row.Supplier.Code, row.Supplier.Name));
        Assert.Equal((itemId, "GI000001", "Hàu giống 3 cm"), (row.Item.Id, row.Item.Code, row.Item.Name));
        Assert.Equal((productId, "SP000001", "Hàu béo"), (row.Product!.Id, row.Product.Code, row.Product.Name));
        Assert.Equal(("con", 100m, 10m, 15m, 5m, 30m, 5m), (row.UnitName, row.ReceivedQty, row.ReturnedQty, row.NursingQty, row.NurseryLossQty, row.SeededQty, row.IssuedOtherQty));
        // 100 - 10 returned - 30 seeded - 5 lost - 5 issued
        Assert.Equal((50m, 35m, SeedLotState.Nursing), (row.RemainingQty, row.AvailableQty, row.State));
        Assert.Null(page.Items[1].Product);
    }

    [Fact]
    public async Task The_export_lists_every_match_in_the_list_order_up_to_the_limit()
    {
        var service = Service(new FakeQuery(Rows()));

        var all = await service.ListAllAsync(new SeedLotListQuery(SortDir: "asc"), 100, CancellationToken.None);
        var capped = await service.ListAllAsync(new SeedLotListQuery(State: "FullySeeded", SortDir: "asc"), 2, CancellationToken.None);
        var refused = await service.ListAllAsync(new SeedLotListQuery(State: "Bogus"), 100, CancellationToken.None);

        Assert.Equal(6, all.Value!.Count);
        Assert.Equal(["PN000011-1", "PN000012-2"], capped.Value!.Select(r => r.SeedLotCode).ToArray());
        Assert.False(refused.IsSuccess);
    }

    [Fact]
    public async Task One_lot_comes_with_its_batches_seedings_returns_issues_cycles_and_harvest_lots_oldest_first()
    {
        var lot = Lot("PN000010", 1, Figures(nurseryIn: 30m));
        var cycle = Guid.NewGuid();
        var query = new FakeQuery([lot])
        {
            Detail = new SeedLotDetailRows(
                [new(Guid.NewGuid(), "NU000002", StatusIds.NurseryBatch.Nursing, 10m, null, new DateOnly(2026, 11, 12)), new(Guid.NewGuid(), "NU000001", StatusIds.NurseryBatch.Completed, 20m, 18m, new DateOnly(2026, 11, 11))],
                [new(Guid.NewGuid(), cycle, "B1-01", "KV01", 15m, new DateOnly(2026, 11, 14))],
                [new(Guid.NewGuid(), "TH000001", 5m, Received.AddDays(3), StatusIds.PurchaseReturn.Recorded)],
                [new(Guid.NewGuid(), "XH000001", InputIssueKind.Disposal, 2m, Received.AddDays(4))],
                [new(cycle, "B1-01", "KV01", StatusIds.CultivationCycle.Open, "SP000001")],
                [new(Guid.NewGuid(), "KV01-B1-01-20261120", StatusIds.HarvestLot.AwaitingWeighing, null, 120m)]),
        };

        var found = await Service(query).GetAsync(lot.LineId, CancellationToken.None);
        var missing = await Service(query).GetAsync(Guid.NewGuid(), CancellationToken.None);

        var detail = found.Value!;
        Assert.Equal(("PN000010-1", SeedLotState.Nursing), (detail.SeedLotCode, detail.State));
        Assert.Equal(["NU000001", "NU000002"], detail.NurseryBatches.Select(b => b.Code).ToArray());
        Assert.Equal((StatusIds.NurseryBatch.Completed, "Status 4002", 18m), (detail.NurseryBatches[0].StatusId, detail.NurseryBatches[0].Status, detail.NurseryBatches[0].Survivors!.Value));
        // A session records a day: it is read at the start of that day (UTC+7).
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.FromHours(7)), detail.Seedings.Single().SeededAt);
        Assert.Equal(("TH000001", StatusIds.PurchaseReturn.Recorded), (detail.Returns.Single().Code, detail.Returns.Single().StatusId));
        Assert.Equal((InputIssueKind.Disposal, 2m), (detail.Issues.Single().Kind, detail.Issues.Single().Quantity));
        Assert.Equal(("SP000001", 1, 1), (detail.Cycles.Single().ProductCode, detail.HarvestLots.Count, detail.Seedings.Count));
        Assert.Equal(ErrorKind.NotFound, missing.Error!.Kind);
    }
}
