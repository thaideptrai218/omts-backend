using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// PR #12 review fixes: full return of a discounted receipt (C1), required draft rowVersion (I3), list limits (I2), draft
/// validation (S1), undefined enums (S2), item usage (S3) and invoice photos on Drafts only (S4).
/// </summary>
public class PurchasingReviewFixTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const decimal Payable = 2_999_000;

    /// <summary>The reviewer's example: 3 lines of 1,000,000 and a 1,000 discount, each line share rounds up to 1,000,000.</summary>
    private static async Task<(PurchasingWorld World, JsonElement Receipt, Guid[] Lines)> ThreeLineReceiptAsync(AuthApiFactory factory, bool paid)
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(1_000, paid ? Payable : 0,
            new LineBody(world.Seed, 1, 1_000_000), new LineBody(world.Material, 1, 1_000_000), new LineBody(world.Other, 1, 1_000_000));
        return (world, receipt, [receipt.LineOf(world.Seed), receipt.LineOf(world.Material), receipt.LineOf(world.Other)]);
    }

    private static ReturnBody Split(bool paid, decimal value, params Guid[] lines) => new()
    {
        RefundAmount = paid ? value : 0, RefundMethod = paid ? "Cash" : null, DebtReductionAmount = paid ? 0 : value,
        Lines = lines.Select(l => new ReturnLineBody(l, 1)).ToList(),
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C1_a_receipt_with_a_discount_can_be_returned_in_full_in_one_return(bool paid)
    {
        var (world, receipt, lines) = await ThreeLineReceiptAsync(factory, paid);

        var view = await (await world.Owner.PostReturnAsync(receipt.Id(), Split(paid, Payable, lines))).ReadAsync(HttpStatusCode.Created);

        Assert.Equal((Payable, 0m), (view.Money("returned"), view.Money("outstanding")));
        Assert.All(view.GetProperty("lines").EnumerateArray(), l => Assert.Equal(0m, l.Money("returnable")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C1_a_receipt_with_a_discount_can_be_returned_line_by_line_and_the_last_line_takes_the_rounding(bool paid)
    {
        var (world, receipt, lines) = await ThreeLineReceiptAsync(factory, paid);

        await (await world.Owner.PostReturnAsync(receipt.Id(), Split(paid, 1_000_000, lines[0]))).ReadAsync(HttpStatusCode.Created);
        await (await world.Owner.PostReturnAsync(receipt.Id(), Split(paid, 1_000_000, lines[1]))).ReadAsync(HttpStatusCode.Created);
        // The last line is worth what is left of the payable amount, not its rounded share of 1,000,000.
        var tooMuch = await world.Owner.PostReturnAsync(receipt.Id(), Split(paid, 1_000_000, lines[2]));
        await tooMuch.ProblemAsync(HttpStatusCode.BadRequest, "MSG45");
        var last = await (await world.Owner.PostReturnAsync(receipt.Id(), Split(paid, 999_000, lines[2]))).ReadAsync(HttpStatusCode.Created);

        Assert.Equal(Payable, last.Money("returned"));
        Assert.Equal(0m, last.Money("outstanding"));
    }

    [Fact]
    public void C1_the_fit_caps_the_total_and_puts_the_remainder_on_the_last_lines()
    {
        Assert.Equal([1_000_000m, 1_000_000m, 999_000m], Fit([1_000_000, 1_000_000, 1_000_000], 2_999_000, final: true));
        Assert.Equal([1_000_000m, 999_000m], Fit([1_000_000, 1_000_000], 1_999_000, final: false));
        Assert.Equal([500_000m, 0m], Fit([500_000, 500_000], 500_000, final: false));
        Assert.Equal([2_000_000m], Fit([1_999_000], 2_000_000, final: true));
        Assert.Equal([1_000_000m], Fit([1_000_000], 5_000_000, final: false));
    }

    private static decimal[] Fit(decimal[] values, decimal remaining, bool final) =>
        OMTS.Application.Features.Purchasing.PurchaseMoney.FitToReceipt(values, remaining, final);

    [Fact]
    public async Task I3_a_draft_edit_needs_the_row_version_and_a_stale_one_is_MSG06()
    {
        var world = await factory.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 100_000)));
        var url = $"/api/purchase-receipts/{draft.Id()}";
        var edit = world.Body(new LineBody(world.Other, 2, 100_000));

        Assert.Equal("RowVersion", await (await world.Owner.PutAsJsonAsync(url, edit)).FieldAsync(HttpStatusCode.BadRequest, "MSG01"));
        await (await world.Owner.PutAsJsonAsync(url, edit with { RowVersion = Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8]) })).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        var saved = await (await world.Owner.PutAsJsonAsync(url, edit with { RowVersion = draft.GetProperty("rowVersion").GetString() })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(2m, saved.GetProperty("lines")[0].Money("quantity"));
    }

    [Fact]
    public async Task S1_a_draft_checks_the_raft_zone_the_scale_and_the_upper_bounds()
    {
        var world = await factory.WorldAsync();
        var (_, foreignRaft) = await factory.ZoneWithRaftAsync();
        LineBody Line(decimal quantity, decimal price) => new(world.Other, quantity, price);

        Assert.Equal("Raft", await (await world.Owner.PostReceiptAsync(world.Body(Line(1, 1000)) with { RaftId = foreignRaft.Id })).FieldAsync(HttpStatusCode.BadRequest, "MSG01"));
        Assert.Equal("Line 1", await (await world.Owner.PostReceiptAsync(world.Body(Line(1, 1000.5m)))).FieldAsync(HttpStatusCode.BadRequest, "MSG59"));
        Assert.Equal("Line 1", await (await world.Owner.PostReceiptAsync(world.Body(Line(1.2345m, 1000)))).FieldAsync(HttpStatusCode.BadRequest, "MSG59"));
        Assert.Equal("Line 1", await (await world.Owner.PostReceiptAsync(world.Body(Line(999_999_999, 999_999_999_999)))).FieldAsync(HttpStatusCode.BadRequest, "MSG59"));
        Assert.Equal("Line 2", await (await world.Owner.PostReceiptAsync(world.Body(Line(1, 1000), Line(0, 1000)))).FieldAsync(HttpStatusCode.BadRequest, "MSG59"));
        Assert.Equal("Discount amount", await (await world.Owner.PostReceiptAsync(world.Body(Line(1, 1000)) with { DiscountAmount = 1_000_000_000_000_000 })).FieldAsync(HttpStatusCode.BadRequest, "MSG01"));
        await (await world.Owner.PostReceiptAsync(world.Body(Line(1.234m, 1000)) with { RaftId = world.Raft.Id })).ReadAsync(HttpStatusCode.Created);
    }

    [Fact]
    public async Task S2_undefined_payment_methods_are_MSG01()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(0, 0, new LineBody(world.Other, 2, 100_000));
        var line = receipt.LineOf(world.Other);

        var paidNow = await world.Owner.PostAsJsonAsync("/api/purchase-receipts", new
        {
            supplierId = world.SupplierId, zoneId = world.Zone.Id, amountPaidNow = 100_000, paidNowMethod = 99, lines = new[] { new { itemId = world.Other, quantity = 1, unitPrice = 100_000 } },
        });
        var refund = await world.Owner.PostAsJsonAsync($"/api/purchase-receipts/{receipt.Id()}/returns", new
        {
            reason = "Broken", returnDate = PurchasingTestKit.Today, refundAmount = 100_000, refundMethod = 99, lines = new[] { new { receiptLineId = line, quantity = 1 } },
        });

        Assert.Equal("Paid now method", await paidNow.FieldAsync(HttpStatusCode.BadRequest, "MSG01"));
        Assert.Equal("Refund method", await refund.FieldAsync(HttpStatusCode.BadRequest, "MSG01"));
    }

    [Fact]
    public async Task S3_the_item_list_reports_which_items_are_used()
    {
        var world = await factory.WorldAsync();
        await world.CompletedAsync(0, 0, new LineBody(world.Other, 1, 100_000));

        var list = await world.Owner.GetFromJsonAsync<JsonElement>("/api/farming-input-items?pageSize=100");
        var items = (list.ValueKind == JsonValueKind.Array ? list : list.GetProperty("items")).EnumerateArray().ToList();
        bool Used(Guid id) => items.First(i => i.Id() == id).GetProperty("isUsed").GetBoolean();

        Assert.True(Used(world.Other));
        Assert.False(Used(world.Seed));
    }

    [Fact]
    public async Task S4_invoice_photos_go_on_Draft_receipts_only()
    {
        var world = await factory.WorldAsync();
        var completed = await world.CompletedAsync(0, 0, new LineBody(world.Other, 1, 100_000));
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D]);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "files", "invoice.png");

        await (await world.Owner.PostAsync($"/api/purchase-receipts/{completed.Id()}/invoice-photos", form)).ProblemAsync(HttpStatusCode.Conflict, "MSG61");
    }

    [Fact]
    public async Task I2_the_list_period_is_at_most_five_years_and_the_page_is_bounded()
    {
        var world = await factory.WorldAsync();
        await world.CompletedAsync(0, 0, new LineBody(world.Other, 1, 100_000));

        var wide = await world.Owner.GetAsync("/api/purchase-receipts?from=2000-01-01&to=2100-12-31");
        var huge = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts?zoneId={world.Zone.Id}&page={int.MaxValue}&pageSize=100000");

        Assert.Equal("To date", await wide.FieldAsync(HttpStatusCode.BadRequest, "MSG01"));
        Assert.Equal(100, huge.GetProperty("page").GetProperty("pageSize").GetInt32());
        Assert.Equal(1, huge.GetProperty("totals").GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task I2_the_list_pages_in_sql_with_totals_of_all_matches_and_the_supplier_detail_keeps_the_latest_receipts()
    {
        var world = await factory.WorldAsync();
        for (var i = 1; i <= 3; i++)
        {
            await world.CompletedAsync(0, i * 50_000, new LineBody(world.Other, i, 100_000));
        }

        var page = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts?zoneId={world.Zone.Id}&pageSize=2&page=2");
        var supplier = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/suppliers/{world.SupplierId}");

        Assert.Equal((2, 3), (page.GetProperty("page").GetProperty("page").GetInt32(), page.GetProperty("page").GetProperty("total").GetInt32()));
        Assert.Equal(1, page.GetProperty("page").GetProperty("items").GetArrayLength());
        // Payable 100k + 200k + 300k, paid 50k + 100k + 150k: the totals cover all three receipts, not just the page.
        Assert.Equal((3, 600_000m, 300_000m), (page.GetProperty("totals").GetProperty("completedCount").GetInt32(), page.GetProperty("totals").Money("payable"), page.GetProperty("totals").Money("outstanding")));
        Assert.Equal(300_000m, supplier.Money("debt"));
        Assert.Equal(3, supplier.GetProperty("receipts").GetArrayLength());
    }
}
