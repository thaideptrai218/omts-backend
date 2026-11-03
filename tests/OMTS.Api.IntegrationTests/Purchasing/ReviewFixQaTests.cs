using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// QA of the PR #12 review fixes (OMTS-209): full returns of discounted receipts keep Σ returns ≤ payable (C1), list and
/// debt figures come from SQL and page in SQL (I2), a draft edit needs the row version the client read (I3), and the
/// edge values of the draft checks (S1). SQL Server tests cover paging, every filter and the debt figures on real SQL.
/// </summary>
public class ReviewFixQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    /// <summary>Three-line receipts with a discount, so every line share rounds (the reviewer's money case).</summary>
    public static IEnumerable<object[]> ReceiptShapes() =>
    [
        [0, 0], [0, 1], [0, 2],
        [1, 0], [1, 1], [1, 2],
        [2, 0], [2, 1], [2, 2],
    ];

    /// <summary>The three line sets; the discount is in <see cref="Discount"/>.</summary>
    private static (decimal Discount, decimal[] Prices) Shape(int shape) => shape switch
    {
        0 => (1_000, [1_000_000m, 1_000_000m, 1_000_000m]),
        1 => (3_000, [200_000m, 200_000m, 600_000m]),
        _ => (1_000, [333_000m, 333_000m, 333_000m]),
    };

    /// <summary>Paid now: 0 = unpaid, 1 = part paid, 2 = fully paid.</summary>
    private static decimal PaidNow(int mode, decimal payable) => mode switch { 0 => 0, 1 => 400_000, _ => payable };

    /// <summary>A return of <paramref name="total"/> that refunds what was paid (cash) and reduces the debt by the rest.</summary>
    private static ReturnBody Settle(decimal total, decimal netPaid, params ReturnLineBody[] lines) => new()
    {
        RefundAmount = Math.Min(netPaid, total),
        RefundMethod = Math.Min(netPaid, total) > 0 ? "Cash" : null,
        DebtReductionAmount = total - Math.Min(netPaid, total),
        Lines = lines,
    };

    [Theory]
    [MemberData(nameof(ReceiptShapes))]
    public async Task C1_one_return_of_every_line_is_worth_exactly_the_payable_left(int shape, int mode)
    {
        var world = await factory.WorldAsync();
        var (discount, prices) = Shape(shape);
        var subtotal = prices.Sum();
        var receipt = await world.CompletedAsync(discount, PaidNow(mode, subtotal - discount),
            new LineBody(world.Seed, 1, prices[0]), new LineBody(world.Material, 1, prices[1]), new LineBody(world.Other, 1, prices[2]));
        var payable = receipt.Money("payable");
        var netPaid = PaidNow(mode, payable);
        var lines = new[] { receipt.LineOf(world.Seed), receipt.LineOf(world.Material), receipt.LineOf(world.Other) };

        // The return answers with the receipt view: its figures after the return.
        var after = await (await world.Owner.PostReturnAsync(receipt.Id(), Settle(payable, netPaid, lines.Select(l => new ReturnLineBody(l, 1)).ToArray())))
            .ReadAsync(HttpStatusCode.Created);

        Assert.Equal((payable, 0m), (after.Money("returned"), after.Money("outstanding")));
        Assert.All(after.GetProperty("lines").EnumerateArray(), l => Assert.Equal(0m, l.Money("returnable")));
    }

    [Fact]
    public async Task C1_a_line_return_followed_by_every_other_line_in_one_return_returns_exactly_the_payable()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(1_000, 2_999_000,
            new LineBody(world.Seed, 1, 1_000_000), new LineBody(world.Material, 1, 1_000_000), new LineBody(world.Other, 1, 1_000_000));
        var lines = new[] { receipt.LineOf(world.Seed), receipt.LineOf(world.Material), receipt.LineOf(world.Other) };

        // Fully paid: the first line is refunded in cash, the rest of the payable is the net paid left.
        await (await world.Owner.PostReturnAsync(receipt.Id(), Settle(1_000_000, 2_999_000, new ReturnLineBody(lines[0], 1))))
            .ReadAsync(HttpStatusCode.Created);
        var rest = await (await world.Owner.PostReturnAsync(receipt.Id(), Settle(1_999_000, 1_999_000, new ReturnLineBody(lines[1], 1), new ReturnLineBody(lines[2], 1))))
            .ReadAsync(HttpStatusCode.Created);

        Assert.Equal((2_999_000m, 0m), (rest.Money("returned"), rest.Money("outstanding")));
    }

    [Fact]
    public async Task C1_stocked_goods_stay_paid_and_the_unstocked_rest_is_returned_at_its_value()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(0, 0,
            new LineBody(world.Seed, 100, 10_000), new LineBody(world.Other, 1, 1_000_000));
        var seedLine = receipt.LineOf(world.Seed);
        var otherLine = receipt.LineOf(world.Other);
        await factory.StockAsync(world.Raft.Id, seedLine, 50);

        // The seed line is not fully returned (50 stocked), so the exact-rest rule does not apply: the values are the lines' own.
        await (await world.Owner.PostReturnAsync(receipt.Id(), Settle(1_000_000, 0, new ReturnLineBody(otherLine, 1)))).ReadAsync(HttpStatusCode.Created);
        var seed = await (await world.Owner.PostReturnAsync(receipt.Id(), Settle(500_000, 0, new ReturnLineBody(seedLine, 50)))).ReadAsync(HttpStatusCode.Created);

        Assert.Equal((1_500_000m, 500_000m), (seed.Money("returned"), seed.Money("outstanding")));
        var again = await world.Owner.PostReturnAsync(receipt.Id(), Settle(1_000, 0, new ReturnLineBody(seedLine, 1)));
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task C1_a_full_return_clears_the_supplier_debt_and_the_receipt_outstanding()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(1_000, 0,
            new LineBody(world.Seed, 1, 1_000_000), new LineBody(world.Material, 1, 1_000_000), new LineBody(world.Other, 1, 1_000_000));
        var lines = new[] { receipt.LineOf(world.Seed), receipt.LineOf(world.Material), receipt.LineOf(world.Other) };
        await (await world.Owner.PostReturnAsync(receipt.Id(), Settle(2_999_000, 0, lines.Select(l => new ReturnLineBody(l, 1)).ToArray())))
            .ReadAsync(HttpStatusCode.Created);

        var debts = await (await world.Owner.GetAsync("/api/suppliers/debts")).ReadAsync(HttpStatusCode.OK);
        var row = debts.EnumerateArray().Single(r => r.GetProperty("supplierId").GetGuid() == world.SupplierId);
        var detail = await (await world.Owner.GetAsync($"/api/suppliers/{world.SupplierId}/debt")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal((2_999_000m, 2_999_000m, 0m), (row.Money("payable"), row.Money("returned"), row.Money("outstanding")));
        Assert.Equal(0m, detail.GetProperty("totals").Money("outstanding"));
    }

    [Fact]
    public async Task I2_list_totals_are_the_sum_of_the_completed_rows_over_every_page_and_count_every_match()
    {
        var world = await factory.WorldAsync();
        var first = await world.CompletedAsync(1_000, 400_000, new LineBody(world.Seed, 1, 1_000_000), new LineBody(world.Other, 2, 500_000));
        var second = await world.CompletedAsync(0, 0, new LineBody(world.Other, 1, 500_000));
        await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 100_000)));
        var cancelled = await world.CompletedAsync(0, 0, new LineBody(world.Other, 1, 200_000));
        await (await world.Owner.CancelAsync(cancelled.Id(), "Nhập nhầm", null)).ReadAsync(HttpStatusCode.OK);

        var query = $"/api/purchase-receipts?zoneId={world.Zone.Id}&supplierId={world.SupplierId}&pageSize=2";
        var page1 = await (await world.Owner.GetAsync($"{query}&page=1")).ReadAsync(HttpStatusCode.OK);
        var page2 = await (await world.Owner.GetAsync($"{query}&page=2")).ReadAsync(HttpStatusCode.OK);
        var totals = page1.GetProperty("totals");
        var rows = page1.GetProperty("page").GetProperty("items").EnumerateArray().Concat(page2.GetProperty("page").GetProperty("items").EnumerateArray())
            .Where(r => r.GetProperty("statusId").GetInt32() == StatusIds.PurchaseReceipt.Completed).ToList();

        Assert.Equal(4, totals.GetProperty("count").GetInt32());
        Assert.Equal(2, totals.GetProperty("completedCount").GetInt32());
        Assert.Equal(rows.Sum(r => r.Money("subtotal")), totals.Money("subtotal"));
        Assert.Equal(rows.Sum(r => r.Money("payable")), totals.Money("payable"));
        Assert.Equal(rows.Sum(r => r.Money("paid")), totals.Money("paid"));
        Assert.Equal(rows.Sum(r => r.Money("outstanding")), totals.Money("outstanding"));
        Assert.Contains(first.Id(), rows.Select(r => r.Id()));
        Assert.Contains(second.Id(), rows.Select(r => r.Id()));
    }

    [Fact]
    public async Task I2_pages_are_stable_when_receipts_share_a_date_and_a_huge_page_is_empty()
    {
        var world = await factory.WorldAsync();
        for (var i = 0; i < 5; i++)
        {
            await world.CompletedAsync(0, 0, new LineBody(world.Other, 1, 100_000));
        }

        var seen = new List<string>();
        for (var page = 1; page <= 3; page++)
        {
            var body = await (await world.Owner.GetAsync($"/api/purchase-receipts?zoneId={world.Zone.Id}&pageSize=2&page={page}")).ReadAsync(HttpStatusCode.OK);
            seen.AddRange(body.GetProperty("page").GetProperty("items").EnumerateArray().Select(r => r.GetProperty("code").GetString()!));
        }

        Assert.Equal(5, seen.Distinct().Count());
        Assert.Equal(5, seen.Count);

        var huge = await (await world.Owner.GetAsync($"/api/purchase-receipts?zoneId={world.Zone.Id}&pageSize=100&page={int.MaxValue}")).ReadAsync(HttpStatusCode.OK);
        Assert.Empty(huge.GetProperty("page").GetProperty("items").EnumerateArray());
        Assert.Equal(5, huge.GetProperty("totals").GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task I2_the_period_is_at_most_five_years_for_list_export_and_raft_cost()
    {
        var world = await factory.WorldAsync();
        var over = "from=2000-01-01&to=2005-01-02";
        var exact = "from=2000-01-01&to=2005-01-01";

        Assert.Equal("To date", await (await world.Owner.GetAsync($"/api/purchase-receipts?{over}")).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("To date", await (await world.Owner.GetAsync($"/api/purchase-receipts/export?{over}")).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("To date", await (await world.Owner.GetAsync($"/api/purchase-receipts/raft-material-costs?{over}")).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal(HttpStatusCode.OK, (await world.Owner.GetAsync($"/api/purchase-receipts?{exact}")).StatusCode);
    }

    [Fact]
    public async Task I3_a_draft_edit_without_a_rowVersion_is_MSG01_and_the_version_read_saves()
    {
        var world = await factory.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 100_000)));
        var url = $"/api/purchase-receipts/{draft.Id()}";
        var edit = world.Body(new LineBody(world.Other, 2, 100_000));

        Assert.Equal("RowVersion", await (await world.Owner.PutAsJsonAsync(url, edit)).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        var saved = await (await world.Owner.PutAsJsonAsync(url, edit with { RowVersion = draft.GetProperty("rowVersion").GetString() })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(2m, saved.GetProperty("lines")[0].Money("quantity"));
    }

    [Theory]
    [InlineData("0.001", 1_000_000L, true)]
    [InlineData("0.0005", 1_000_000L, false)]
    [InlineData("1000000000", 1L, true)]
    [InlineData("1000000001", 1L, false)]
    [InlineData("1", 100_000_000_000_000L, true)]
    [InlineData("1", 100_000_000_000_001L, false)]
    [InlineData("2", 50_000_000_000_000L, true)]
    [InlineData("2", 50_000_000_000_001L, false)]
    public async Task S1_draft_scale_and_upper_bound_edges(string quantity, long price, bool accepted)
    {
        var world = await factory.WorldAsync();
        var response = await world.Owner.PostReceiptAsync(world.Body(new LineBody(world.Other, decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture), price)));

        if (accepted)
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        else
        {
            Assert.Equal("Line 1", await response.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        }
    }

    [Fact]
    public async Task S1_a_completed_receipt_cannot_discount_more_than_its_subtotal()
    {
        var world = await factory.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 100_000)) with { DiscountAmount = 101_000 });

        var response = await world.Owner.PostAsync($"/api/purchase-receipts/{draft.Id()}/complete", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Discount amount", problem.GetProperty("field").GetString());
    }

    // ───────────────────────── SQL Server ─────────────────────────

    [SqlServerFact]
    public async Task SQL_list_pages_and_totals_in_sql_and_a_huge_page_does_not_overflow_the_offset()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        for (var i = 0; i < 4; i++)
        {
            await world.CompletedAsync(0, 0, new LineBody(world.Other, 1, 100_000));
        }
        await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 100_000)));

        var first = await (await world.Owner.GetAsync($"/api/purchase-receipts?zoneId={world.Zone.Id}&pageSize=3&page=1")).ReadAsync(HttpStatusCode.OK);
        var second = await (await world.Owner.GetAsync($"/api/purchase-receipts?zoneId={world.Zone.Id}&pageSize=3&page=2")).ReadAsync(HttpStatusCode.OK);
        var huge = await (await world.Owner.GetAsync($"/api/purchase-receipts?zoneId={world.Zone.Id}&pageSize=100&page={int.MaxValue}")).ReadAsync(HttpStatusCode.OK);

        var codes = first.GetProperty("page").GetProperty("items").EnumerateArray().Concat(second.GetProperty("page").GetProperty("items").EnumerateArray())
            .Select(r => r.GetProperty("code").GetString()).ToList();
        Assert.Equal(5, codes.Distinct().Count());
        Assert.Equal(5, first.GetProperty("totals").GetProperty("count").GetInt32());
        Assert.Equal(4, first.GetProperty("totals").GetProperty("completedCount").GetInt32());
        Assert.Empty(huge.GetProperty("page").GetProperty("items").EnumerateArray());
    }

    [SqlServerFact]
    public async Task SQL_every_list_filter_alone_and_every_debt_and_cost_query_translates_on_sql_server()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var seed = await world.CompletedAsync(0, 0, new LineBody(world.Seed, 10, 10_000), new LineBody(world.Material, 2, 10_000));
        var code = seed.GetProperty("code").GetString()!;
        var filters = new[]
        {
            $"itemGroup=Seed", $"itemGroup=RaftMaterial", $"itemId={world.Seed}", $"zoneId={world.Zone.Id}", $"raftId={world.Raft.Id}",
            $"supplierId={world.SupplierId}", $"statusId={StatusIds.PurchaseReceipt.Completed}", $"statusId={StatusIds.PurchaseReceipt.Draft}",
            $"from={Today.AddDays(-1):yyyy-MM-dd}", $"to={Today:yyyy-MM-dd}", $"code={code[3..].ToLowerInvariant()}", "from=2000-01-01&to=2005-01-01",
        };

        foreach (var filter in filters)
        {
            var list = await world.Owner.GetAsync($"/api/purchase-receipts?{filter}&pageSize=5");
            Assert.True(list.StatusCode == HttpStatusCode.OK, $"{filter}: {await list.Content.ReadAsStringAsync()}");
            var export = await world.Owner.GetAsync($"/api/purchase-receipts/export?{filter}");
            Assert.True(export.StatusCode == HttpStatusCode.OK, $"export {filter}");
        }

        Assert.Equal(HttpStatusCode.OK, (await world.Owner.GetAsync($"/api/purchase-receipts/raft-material-costs?zoneId={world.Zone.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await world.Owner.GetAsync("/api/suppliers/debts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await world.Owner.GetAsync($"/api/suppliers/{world.SupplierId}/debt")).StatusCode);
    }

    [SqlServerFact]
    public async Task SQL_supplier_debt_list_and_detail_follow_the_old_rules_cancelled_returns_and_unallocated_payments()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var r1 = await world.CompletedAsync(0, 300_000, new LineBody(world.Seed, 10, 100_000), new LineBody(world.Other, 1, 500_000));
        var r2 = await world.CompletedAsync(0, 0, new LineBody(world.Other, 2, 200_000));
        // Cancelled with its payment kept: the receipt leaves the debt, the 50,000 becomes unallocated.
        var cancelled = await world.CompletedAsync(0, 50_000, new LineBody(world.Other, 1, 100_000));
        await (await world.Owner.CancelAsync(cancelled.Id(), "Nhập nhầm", false)).ReadAsync(HttpStatusCode.OK);
        await (await world.Owner.PostReturnAsync(r1.Id(), Settle(500_000, 300_000, new ReturnLineBody(r1.LineOf(world.Other), 1)))).ReadAsync(HttpStatusCode.Created);

        // Payable 1,500,000 + 400,000; returned 500,000; paid 300,000 refunded 300,000; unallocated 50,000.
        var row = (await (await world.Owner.GetAsync("/api/suppliers/debts")).ReadAsync(HttpStatusCode.OK))
            .EnumerateArray().Single(r => r.GetProperty("supplierId").GetGuid() == world.SupplierId);
        var detail = await (await world.Owner.GetAsync($"/api/suppliers/{world.SupplierId}/debt")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal((1_900_000m, 500_000m, 300_000m, 300_000m, 50_000m, 1_350_000m),
            (row.Money("payable"), row.Money("returned"), row.Money("paid"), row.Money("refunded"), row.Money("unallocated"), row.Money("outstanding")));
        Assert.Equal(row.Money("outstanding"), detail.GetProperty("totals").Money("outstanding"));
        Assert.Equal(2, detail.GetProperty("receipts").GetArrayLength());
        Assert.DoesNotContain(cancelled.Id(), detail.GetProperty("receipts").EnumerateArray().Select(r => r.GetProperty("receiptId").GetGuid()));
    }

}
