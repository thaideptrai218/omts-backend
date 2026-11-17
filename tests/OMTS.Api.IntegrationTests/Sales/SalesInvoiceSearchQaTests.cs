using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSearchTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>Independent QA of SALE-09 steps 1-3: zone visibility (BR-SALE-08, BR-FARM-08), the period, the filters together, paging and ties.</summary>
public class SalesInvoiceSearchQaTests
{
    private static readonly DateOnly Today = PurchaseRequestTestKit.Today;

    internal static SalesInvoice NewInvoice(Guid zoneId, Guid accountId, DateOnly? date = null, DateTimeOffset? at = null, decimal total = 100_000m, string? code = null) => new()
    {
        Code = code ?? "HD" + AcsTestKit.NewCode(), InvoicedAt = at ?? DateTimeOffset.UtcNow, InvoiceDate = date ?? Today, ZoneId = zoneId, Subtotal = total, Total = total,
        PaymentTerm = SalesPaymentTerm.PayNow, CreatedByAccountId = accountId,
    };

    private static async Task<int> SheetRowsAsync(HttpResponseMessage file)
    {
        using var workbook = new XLWorkbook(await file.Content.ReadAsStreamAsync());
        return workbook.Worksheet(SalesInvoiceExportSheets.ListSheet).LastRowUsed()!.RowNumber();
    }

    // BR-SALE-06/08: one invoice may mix lots of several zones; the zone of the invoice alone decides who sees it, and its whole value counts there.
    [Fact]
    public async Task An_invoice_with_lines_of_two_zones_is_seen_only_through_its_own_zone()
    {
        using var api = new AuthApiFactory();
        var a = await api.SaleSetupAsync();
        var b = await api.SaleSetupAsync();
        var body = Invoice(a.CustomerId, [Line(a.ProductId, a.LotId, 1m), Line(b.ProductId, b.LotId, 1m)], Cash(60_000m), a.LotId, b.LotId).With("zoneId", a.ZoneId);
        var mixed = (await a.Owner.Client.SellAsync(body)).GetProperty("invoice");
        Assert.Equal(a.ZoneId, mixed.GetProperty("zone").Id());
        var accountantB = (await api.StaffAsync(await api.ZoneEntityAsync(b.ZoneId), StaffRole.Accountant)).Client;

        var seenInA = await a.Manager.Client.InvoiceListAsync(Query(("harvestLotId", b.LotId)));
        Assert.Equal([mixed.Text("code")], seenInA.Codes());
        Assert.Equal(60_000m, seenInA.Money("completedTotal"));
        var lineZones = (await a.Manager.Client.InvoiceAsync(mixed.Id())).GetProperty("lines").EnumerateArray().Count();
        Assert.Equal(2, lineZones);

        foreach (var client in new[] { b.Manager.Client, accountantB })
        {
            var ofB = await client.InvoiceListAsync();
            Assert.Equal((0, 0m, "MSG-GEN-10"), (ofB.Rows().Count, ofB.Money("completedTotal"), ofB.Text("messageCode")));
            Assert.Empty((await client.InvoiceListAsync(Query(("harvestLotId", b.LotId)))).Rows());
            Assert.Empty((await client.InvoiceListAsync(Query(("raftId", b.RaftId)))).Rows());
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetInvoiceAsync(mixed.Id())).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await b.Manager.Client.PrintInvoiceAsync(mixed.Id())).StatusCode);
        Assert.Equal(2, await SheetRowsAsync(await b.Manager.Client.ExportInvoicesAsync()));
    }

    // BR-FARM-08: pairs in two zones open both, a zone filter narrows, and a pair of a third zone adds nothing.
    [Fact]
    public async Task A_person_with_pairs_in_some_zones_sees_exactly_those_zones()
    {
        using var api = new AuthApiFactory();
        var a = await api.SaleSetupAsync();
        var b = await api.SaleSetupAsync();
        var c = await api.SaleSetupAsync();
        var inA = await a.Manager.Client.SoldAsync(a, a.LotId, 1m);
        var inB = await b.Manager.Client.SoldAsync(b, b.LotId, 2m);
        await c.Manager.Client.SoldAsync(c, c.LotId, 4m);
        var (account, _) = await api.StaffAccountAsync(await api.ZoneEntityAsync(a.ZoneId));
        await api.GrantAsync(account, StaffRole.ZoneManager, await api.ZoneEntityAsync(b.ZoneId));
        var client = await api.LoginAsync(account.Username);

        var list = await client.InvoiceListAsync();
        Assert.Equal(new[] { inA.Text("code"), inB.Text("code") }.Order(), list.Codes().Order());
        Assert.Equal(90_000m, list.Money("completedTotal"));
        Assert.Equal([inB.Text("code")], (await client.InvoiceListAsync(Query(("zoneId", b.ZoneId)))).Codes());
        var refused = await client.GetInvoicesAsync(Query(("zoneId", c.ZoneId)));
        Assert.Equal((HttpStatusCode.Forbidden, "MSG-GEN-08"), (refused.StatusCode, await refused.ErrorCodeAsync()));
        Assert.Equal(4, await SheetRowsAsync(await client.ExportInvoicesAsync()));
    }

    // GFR-02: a revoke applies from the next action, also through the rights cache; a discontinued zone keeps its pairs for its history (BR-FARM-08).
    [Fact]
    public async Task A_revoked_pair_closes_its_zone_from_the_next_action_and_a_locked_account_sees_nothing()
    {
        using var api = new AuthApiFactory();
        var a = await api.SaleSetupAsync();
        var b = await api.SaleSetupAsync();
        var inA = await a.Manager.Client.SoldAsync(a, a.LotId);
        var inB = await b.Manager.Client.SoldAsync(b, b.LotId);
        var (account, pairA) = await api.StaffAccountAsync(await api.ZoneEntityAsync(a.ZoneId));
        await api.GrantAsync(account, StaffRole.ZoneManager, await api.ZoneEntityAsync(b.ZoneId));
        var client = await api.LoginAsync(account.Username);
        Assert.Equal(2, (await client.InvoiceListAsync()).Rows().Count);

        Assert.Equal(HttpStatusCode.OK, (await a.Owner.Client.PostAsJsonAsync($"/api/accounts/{account.Id}/grants/{pairA.Id}/revoke", new { reason = "Moved" })).StatusCode);

        Assert.Equal([inB.Text("code")], (await client.InvoiceListAsync()).Codes());
        foreach (var response in new[] { await client.GetInvoiceAsync(inA.Id()), await client.PrintInvoiceAsync(inA.Id()), await client.GetInvoicesAsync(Query(("zoneId", a.ZoneId))) })
        {
            Assert.Equal((HttpStatusCode.Forbidden, "MSG-GEN-08"), (response.StatusCode, await response.ErrorCodeAsync()));
        }

        Assert.Equal(3, await SheetRowsAsync(await client.ExportInvoicesAsync()));
        Assert.Equal(HttpStatusCode.OK, (await a.Owner.Client.PostAsJsonAsync($"/api/accounts/{account.Id}/lock", new { reason = "Left" })).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetInvoicesAsync()).StatusCode);
    }

    [Fact]
    public async Task The_manager_of_a_discontinued_zone_still_sees_its_invoices()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        await api.UpdateAsync<FarmingZone>(sale.ZoneId, z => z.StatusId = StatusIds.Usage.Discontinued);
        var manager = await api.LoginAsync(sale.Manager.Account.Username);

        Assert.Equal([invoice.Text("code")], (await manager.InvoiceListAsync()).Codes());
        Assert.Equal(HttpStatusCode.OK, (await manager.GetInvoiceAsync(invoice.Id())).StatusCode);
    }

    // Mixed pairs: the Zone Manager pair and the Accountant pair both view and export (the Accountant exports since the PM answer of 12/11/2026), so the file holds the same invoices as the screen.
    [Fact]
    public async Task With_a_zone_manager_pair_and_an_accountant_pair_the_list_and_the_file_show_both_zones_and_a_third_zone_is_refused()
    {
        using var api = new AuthApiFactory();
        var a = await api.SaleSetupAsync();
        var b = await api.SaleSetupAsync();
        var c = await api.SaleSetupAsync();
        await a.Manager.Client.SoldAsync(a, a.LotId, 1m);
        await b.Manager.Client.SoldAsync(b, b.LotId, 2m);
        await c.Manager.Client.SoldAsync(c, c.LotId, 4m);
        var (account, _) = await api.StaffAccountAsync(await api.ZoneEntityAsync(a.ZoneId));
        await api.GrantAsync(account, StaffRole.Accountant, await api.ZoneEntityAsync(b.ZoneId));
        var client = await api.LoginAsync(account.Username);

        Assert.Equal(2, (await client.InvoiceListAsync()).Rows().Count);
        using var workbook = new XLWorkbook(await (await client.ExportInvoicesAsync()).Content.ReadAsStreamAsync());
        var sheet = workbook.Worksheet(SalesInvoiceExportSheets.ListSheet);
        Assert.Equal((4, 90_000m), (sheet.LastRowUsed()!.RowNumber(), sheet.Cell(4, 5).GetValue<decimal>()));
        Assert.Equal(HttpStatusCode.OK, (await client.ExportInvoicesAsync(Query(("zoneId", b.ZoneId)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.ExportInvoicesAsync(Query(("zoneId", c.ZoneId)))).StatusCode);
    }

    [Theory]
    [InlineData("from=abc")]
    [InlineData("to=2026-02-30")]
    [InlineData("from=2026-13-01")]
    [InlineData("paid=maybe")]
    [InlineData("page=abc")]
    [InlineData("page=2147483648")]
    [InlineData("zoneId=not-a-guid")]
    [InlineData("customerId=not-a-guid")]
    [InlineData("statusId=x")]
    public async Task A_value_that_cannot_be_read_is_refused_with_msg_gen_01_and_never_500(string query)
    {
        using var api = new AuthApiFactory();
        var owner = await api.OwnerAsync();
        foreach (var path in new[] { "/api/sales-invoices?", "/api/sales-invoices/export?" })
        {
            var response = await owner.Client.GetAsync(path + query);
            Assert.Equal((HttpStatusCode.BadRequest, "MSG-GEN-01"), (response.StatusCode, await response.ErrorCodeAsync()));
        }
    }

    // A form that sends its empty filters: a filter left empty is no filter.
    [Fact]
    public async Task Filters_sent_empty_are_no_filter()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.Manager.Client.SoldAsync(sale, sale.LotId);

        var list = await sale.Manager.Client.InvoiceListAsync("?from=&to=&zoneId=&customerId=&harvestLotId=&raftId=&statusId=&paid=&code=");

        Assert.Single(list.Rows());
    }

    [Fact]
    public async Task The_period_limit_is_exactly_five_years_and_the_extreme_dates_do_not_fail()
    {
        using var api = new AuthApiFactory();
        var owner = (await api.OwnerAsync()).Client;

        Assert.Equal(HttpStatusCode.OK, (await owner.GetInvoicesAsync(Query(("from", Today.AddYears(-5)), ("to", Today)))).StatusCode);
        var tooLong = await owner.GetInvoicesAsync(Query(("from", Today.AddYears(-5).AddDays(-1)), ("to", Today)));
        Assert.Equal((HttpStatusCode.BadRequest, "MSG-GEN-01"), (tooLong.StatusCode, await tooLong.ErrorCodeAsync()));
        Assert.Equal(HttpStatusCode.OK, (await owner.GetInvoicesAsync("?from=0001-01-01&to=0001-01-02")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetInvoicesAsync("?from=9999-12-31&to=9999-12-31")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetInvoicesAsync("?from=9995-01-01&to=9999-12-31")).StatusCode);
    }

    // SALE-09 step 3: "to" is the whole of its day, in Vietnam time; the file shows the local time.
    [Fact]
    public async Task The_last_day_of_the_period_is_included_to_its_last_second_and_the_file_shows_vietnam_time()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var day = Today.AddDays(-3);
        var offset = TimeSpan.FromHours(7);
        var last = NewInvoice(sale.ZoneId, sale.Owner.Account.Id, day, new DateTimeOffset(day.ToDateTime(new TimeOnly(23, 59, 59)), offset));
        var next = NewInvoice(sale.ZoneId, sale.Owner.Account.Id, day.AddDays(1), new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), offset));
        await api.UseDbAsync(db => db.SalesInvoices.AddRange(last, next));
        var range = Query(("from", day), ("to", day));

        Assert.Equal([last.Code], (await sale.Owner.Client.InvoiceListAsync(range)).Codes());
        using var workbook = new XLWorkbook(await (await sale.Owner.Client.ExportInvoicesAsync(range)).Content.ReadAsStreamAsync());
        Assert.Equal(day.ToDateTime(new TimeOnly(23, 59, 59)), workbook.Worksheet(SalesInvoiceExportSheets.ListSheet).Cell(2, 2).GetDateTime());
    }

    // SALE-09 step 2: every filter at once is their intersection.
    [Fact]
    public async Task The_filters_together_are_the_intersection_of_each_alone()
    {
        using var api = new AuthApiFactory();
        var a = await api.SaleSetupAsync();
        var b = await api.SaleSetupAsync();
        var owner = a.Owner.Client;
        var other = (await owner.CreateCustomerAsync()).Id();
        var wanted = (await a.Manager.Client.SellAsync(a.PayLater(a.OtherLotId, 1m))).GetProperty("invoice");
        await a.Manager.Client.SellAsync(a.PayLater(a.LotId, 1m));
        await a.Manager.Client.SoldAsync(a, a.OtherLotId, 1m);
        await a.Manager.Client.SellAsync(a.PayLater(a.OtherLotId, 1m, customerId: other));
        await b.Manager.Client.SellAsync(b.PayLater(b.LotId, 1m, customerId: a.CustomerId));

        var list = await owner.InvoiceListAsync(Query(
            ("from", Today), ("to", Today), ("zoneId", a.ZoneId), ("customerId", a.CustomerId), ("harvestLotId", a.OtherLotId), ("raftId", a.RaftId),
            ("statusId", StatusIds.SalesInvoice.Completed), ("paid", false), ("code", wanted.Text("code")![2..])));

        Assert.Equal([wanted.Text("code")], list.Codes());
        Assert.Equal(ListPrice, list.Money("completedTotal"));
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("[")]
    [InlineData("]")]
    [InlineData("\\")]
    public async Task The_characters_of_like_match_only_a_code_that_holds_them(string character)
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var plain = NewInvoice(sale.ZoneId, sale.Owner.Account.Id, code: "HD" + AcsTestKit.NewCode());
        var special = NewInvoice(sale.ZoneId, sale.Owner.Account.Id, code: "HD" + character + AcsTestKit.NewCode());
        await api.UseDbAsync(db => db.SalesInvoices.AddRange(plain, special));

        Assert.Equal([special.Code], (await sale.Owner.Client.InvoiceListAsync(Query(("code", character)))).Codes());
    }

    [Fact]
    public async Task Paging_edges_answer_a_page_and_never_500()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.UseDbAsync(db => db.SalesInvoices.AddRange(Enumerable.Range(0, 25).Select(_ => NewInvoice(sale.ZoneId, sale.Owner.Account.Id))));
        var owner = sale.Owner.Client;

        Assert.Equal(20, (await owner.InvoiceListAsync()).Rows().Count);
        Assert.Single((await owner.InvoiceListAsync("?pageSize=0")).Rows());
        Assert.Single((await owner.InvoiceListAsync("?pageSize=-5")).Rows());
        var capped = await owner.InvoiceListAsync("?pageSize=2147483647");
        Assert.Equal((25, 100), (capped.Rows().Count, capped.GetProperty("page").GetProperty("pageSize").GetInt32()));
        Assert.Equal(1, (await owner.InvoiceListAsync("?page=0")).GetProperty("page").GetProperty("page").GetInt32());
        Assert.Equal(1, (await owner.InvoiceListAsync("?page=-3")).GetProperty("page").GetProperty("page").GetInt32());
        Assert.Single((await owner.InvoiceListAsync("?page=13&pageSize=2")).Rows());
        var beyond = await owner.InvoiceListAsync("?page=2147483647&pageSize=100");
        Assert.Equal((0, 25, 2_500_000m, JsonNull), (beyond.Rows().Count, beyond.GetProperty("page").GetProperty("total").GetInt32(), beyond.Money("completedTotal"), beyond.GetProperty("message").ValueKind));
    }

    private const System.Text.Json.JsonValueKind JsonNull = System.Text.Json.JsonValueKind.Null;

    // Equal times: every invoice once, in a fixed order, across the pages.
    [Fact]
    public async Task Invoices_of_the_same_second_keep_one_order_across_the_pages()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var at = DateTimeOffset.UtcNow;
        var invoices = Enumerable.Range(0, 7).Select(_ => NewInvoice(sale.ZoneId, sale.Owner.Account.Id, at: at)).ToList();
        await api.UseDbAsync(db => db.SalesInvoices.AddRange(invoices));

        var pages = new List<string>();
        for (var page = 1; page <= 3; page++)
        {
            pages.AddRange((await sale.Owner.Client.InvoiceListAsync($"?pageSize=3&page={page}")).Codes());
        }

        Assert.Equal(invoices.Select(i => i.Code).OrderDescending(StringComparer.Ordinal), pages);
    }

    // SALE-09 step 1: the total of the Completed invoices of the whole list, in the caller's zones, also with the paid filter on a page.
    [Fact]
    public async Task The_completed_total_follows_every_filter_and_zone_and_never_the_page()
    {
        using var api = new AuthApiFactory();
        var a = await api.SaleSetupAsync();
        var b = await api.SaleSetupAsync();
        var first = (await a.Manager.Client.SellAsync(a.PayLater(a.LotId, 1m))).GetProperty("invoice");
        await a.Manager.Client.SellAsync(a.PayLater(a.LotId, 2m));
        await a.Manager.Client.SoldAsync(a, a.LotId, 4m, customerId: (await a.Owner.Client.CreateCustomerAsync()).Id());
        var cancelled = await a.Manager.Client.SoldAsync(a, a.LotId, 8m, walkIn: true);
        await api.UpdateAsync<SalesInvoice>(cancelled.Id(), i => i.StatusId = StatusIds.SalesInvoice.Cancelled);
        await b.Manager.Client.SoldAsync(b, b.LotId, 16m);
        Assert.NotNull(first.Text("code"));
        var manager = a.Manager.Client;

        Assert.Equal((4, 210_000m), Figures(await manager.InvoiceListAsync("?pageSize=1")));
        Assert.Equal((2, 90_000m), Figures(await manager.InvoiceListAsync("?paid=false&pageSize=1")));
        Assert.Equal((1, 120_000m), Figures(await manager.InvoiceListAsync("?paid=true&pageSize=1")));
        Assert.Equal((1, 0m), Figures(await manager.InvoiceListAsync(Query(("statusId", StatusIds.SalesInvoice.Cancelled)))));
    }

    private static (int Total, decimal Completed) Figures(System.Text.Json.JsonElement list) =>
        (list.GetProperty("page").GetProperty("total").GetInt32(), list.Money("completedTotal"));

    // SALE-09 step 1 and no field outside the screen.
    [Fact]
    public async Task A_row_holds_the_fields_of_the_screen_and_nothing_internal()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.Manager.Client.SoldAsync(sale, sale.LotId);

        var row = (await sale.Manager.Client.InvoiceListAsync()).Rows()[0];

        var names = row.EnumerateObject().Select(p => p.Name).ToHashSet();
        foreach (var expected in new[] { "code", "invoicedAt", "zone", "customer", "total", "status", "paymentTerm", "isPaid", "deliveryStatus" })
        {
            Assert.Contains(expected, names);
        }

        foreach (var hidden in new[] { "deduplicationKey", "qrToken", "rowVersion", "createdByAccountId", "payments", "lines", "purchaseRequestId" })
        {
            Assert.DoesNotContain(hidden, names);
        }
    }
}
