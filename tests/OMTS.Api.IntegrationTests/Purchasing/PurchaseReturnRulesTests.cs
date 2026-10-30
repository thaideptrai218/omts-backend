using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>INP-05 rules and error bullets: returnable quantity, money split, dates, grants, views and audit.</summary>
public class PurchaseReturnRulesTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // Seed 100 kg × 10,000, discount 100,000 (payable 900,000 = 90 %), paid now 300,000: outstanding 600,000, net paid 300,000.
    private async Task<(PurchasingWorld World, JsonElement Receipt, Guid SeedLine)> SeedReceiptAsync()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(100_000, 300_000, new LineBody(world.Seed, 100, 10_000));
        return (world, receipt, receipt.LineOf(world.Seed));
    }

    private static ReturnBody Return(Guid lineId, decimal quantity, decimal refund, decimal debtReduction) => new()
    {
        RefundAmount = refund, RefundMethod = refund > 0 ? "Cash" : null, DebtReductionAmount = debtReduction, Lines = [new ReturnLineBody(lineId, quantity)],
    };

    // Done when: cumulative returns stop at received − stocked; refund and debt-reduction cases give the expected outstanding and supplier debt.
    [Fact]
    public async Task Cumulative_returns_stop_at_received_minus_stocked_and_each_split_gives_the_expected_outstanding_and_debt()
    {
        var (world, receipt, line) = await SeedReceiptAsync();
        await factory.StockAsync(world.Raft.Id, line, 30);
        var view = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{receipt.Id()}");
        Assert.Equal((30m, 70m), (view.LineViewOf(world.Seed).Money("stocked"), view.LineViewOf(world.Seed).Money("returnable")));

        // 50 kg: 450,000 = 100,000 refund + 350,000 debt reduction → outstanding 600,000 − 350,000.
        var first = await (await world.Owner.PostReturnAsync(receipt.Id(), Return(line, 50, 100_000, 350_000))).ReadAsync(HttpStatusCode.Created);
        Assert.Equal((450_000m, 100_000m, 250_000m), (first.Money("returned"), first.Money("refunded"), first.Money("outstanding")));
        Assert.Equal(20, first.LineViewOf(world.Seed).Money("returnable"));
        Assert.Equal(250_000, await DebtAsync(world));

        // 20 kg: 180,000 all refunded → the outstanding does not change.
        var second = await (await world.Owner.PostReturnAsync(receipt.Id(), Return(line, 20, 180_000, 0))).ReadAsync(HttpStatusCode.Created);
        Assert.Equal((630_000m, 280_000m, 250_000m, 0m), (second.Money("returned"), second.Money("refunded"), second.Money("outstanding"), second.LineViewOf(world.Seed).Money("returnable")));
        Assert.Equal(70, second.LineViewOf(world.Seed).Money("returnedQuantity"));
        Assert.Equal(250_000, await DebtAsync(world));

        var over = await world.Owner.PostReturnAsync(receipt.Id(), Return(line, 1, 0, 9000));
        var problem = await over.ProblemAsync(HttpStatusCode.BadRequest, "MSG45");
        Assert.Equal(("Line 1", "Returned quantity exceeds the allowed limit (0 kg)."), (problem.GetProperty("field").GetString(), problem.GetProperty("detail").GetString()));
        Assert.Equal(2, await factory.QueryAsync(db => db.PurchaseReturns.CountAsync(r => r.ReceiptId == receipt.Id())));

        var debtRow = (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/suppliers/{world.SupplierId}/debt")).GetProperty("totals");
        Assert.Equal((900_000m, 630_000m, 300_000m, 280_000m), (debtRow.Money("payable"), debtRow.Money("returned"), debtRow.Money("paid"), debtRow.Money("refunded")));
    }

    [Fact]
    public async Task Returned_quantity_above_returnable_names_the_line_and_the_limit_with_its_unit()
    {
        var (world, receipt, line) = await SeedReceiptAsync();
        await factory.StockAsync(world.Raft.Id, line, 30);
        var over = await world.Owner.PostReturnAsync(receipt.Id(), Return(line, 70.5m, 0, 0));
        var problem = await over.ProblemAsync(HttpStatusCode.BadRequest, "MSG45");
        Assert.Equal("Returned quantity exceeds the allowed limit (70 kg).", problem.GetProperty("detail").GetString());
        Assert.Equal(70, problem.Money("limit"));
    }

    [Fact]
    public async Task A_seed_lot_seeded_without_a_quantity_leaves_nothing_to_return()
    {
        var (world, receipt, line) = await SeedReceiptAsync();
        await factory.StockAsync(world.Raft.Id, line, null);
        var view = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{receipt.Id()}");
        Assert.Equal((100m, 0m), (view.LineViewOf(world.Seed).Money("stocked"), view.LineViewOf(world.Seed).Money("returnable")));
        await (await world.Owner.PostReturnAsync(receipt.Id(), Return(line, 1, 0, 9000))).ProblemAsync(HttpStatusCode.BadRequest, "MSG45");
    }

    [Fact]
    public async Task Non_seed_lines_are_never_stocked_and_a_material_return_lowers_the_raft_material_cost()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(0, 0, new LineBody(world.Material, 10, 50_000), new LineBody(world.Other, 4, 25_000));
        Assert.Equal(500_000, (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{world.Raft.Id}")).Money("materialCost"));

        var returned = await (await world.Owner.PostReturnAsync(receipt.Id(), new ReturnBody
        {
            DebtReductionAmount = 600_000,
            Lines = [new ReturnLineBody(receipt.LineOf(world.Material), 10), new ReturnLineBody(receipt.LineOf(world.Other), 4)],
        })).ReadAsync(HttpStatusCode.Created);

        Assert.Equal((600_000m, 0m), (returned.Money("returned"), returned.Money("outstanding")));
        Assert.Equal(0, (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{world.Raft.Id}")).Money("materialCost"));
        Assert.Equal(0, await DebtAsync(world));
    }

    [Fact]
    public async Task Refund_and_debt_reduction_must_add_up_stay_within_outstanding_and_net_paid_and_be_thousands()
    {
        var (world, receipt, line) = await SeedReceiptAsync();
        // 40 kg are worth 360,000; 70 kg 630,000 (above the outstanding 600,000).
        foreach (var (body, code, field, limit) in new (ReturnBody, string, string, decimal?)[]
        {
            (Return(line, 40, 100_000, 100_000), "MSG45", "Refund amount + Debt reduction amount", 360_000),
            (Return(line, 70, 0, 630_000), "MSG45", "Debt reduction amount", 600_000),
            (Return(line, 40, 360_000, 0), "MSG45", "Refund amount", 300_000),
            (Return(line, 40, 1500, 358_500), "MSG41", "Refund amount", null),
            (Return(line, 40, 0, 359_500) with { RefundAmount = 500, RefundMethod = "Cash" }, "MSG41", "Refund amount", null),
            (Return(line, 40, 0, 360_500), "MSG41", "Debt reduction amount", null),
        })
        {
            var problem = await (await world.Owner.PostReturnAsync(receipt.Id(), body)).ProblemAsync(HttpStatusCode.BadRequest, code);
            Assert.Equal(field, problem.GetProperty("field").GetString());
            if (limit is { } expected)
            {
                Assert.Equal(expected, problem.Money("limit"));
            }
        }

        Assert.Equal(0, await factory.QueryAsync(db => db.PurchaseReturns.CountAsync(r => r.ReceiptId == receipt.Id())));
        // Exactly at the refund limit: 300,000 refund (all the net paid) + 60,000 debt reduction for 40 kg worth 360,000.
        await (await world.Owner.PostReturnAsync(receipt.Id(), Return(line, 40, 300_000, 60_000))).ReadAsync(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Reason_date_refund_method_and_lines_are_checked_with_MSG01_or_MSG59()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Seed, 100, 10_000)) with
        {
            ReceiptDate = Today.AddDays(-5), AmountPaidNow = 100_000, PaidNowMethod = "Cash", Complete = true,
        });
        var line = receipt.LineOf(world.Seed);
        var otherReceipt = await world.CompletedAsync(0, 0, new LineBody(world.Other, 1, 1000));
        var valid = Return(line, 1, 0, 10_000);

        foreach (var (body, field) in new (ReturnBody, string)[]
        {
            (valid with { Reason = null }, "Reason"),
            (valid with { Reason = new string('r', 1001) }, "Reason"),
            (valid with { ReturnDate = null }, "Return date"),
            (valid with { ReturnDate = Today.AddDays(-6) }, "Return date"),
            (valid with { ReturnDate = Today.AddDays(1) }, "Return date"),
            (Return(line, 1, 10_000, 0) with { RefundMethod = null }, "Refund method"),
            (valid with { Lines = [] }, "Lines"),
            (valid with { Lines = null }, "Lines"),
            (valid with { Lines = [new ReturnLineBody(otherReceipt.LineOf(world.Other), 1)] }, "Line 1"),
            (valid with { Lines = [new ReturnLineBody(null, 1)] }, "Line 1"),
            (valid with { Lines = [new ReturnLineBody(line, 1), new ReturnLineBody(line, 1)] }, "Lines"),
            (valid with { RefundAmount = -1000 }, "Refund amount"),
        })
        {
            Assert.Equal(field, await (await world.Owner.PostReturnAsync(receipt.Id(), body)).FieldAsync(HttpStatusCode.BadRequest, "MSG01"));
        }

        foreach (var quantity in new decimal?[] { 0, -1, null })
        {
            var response = await world.Owner.PostReturnAsync(receipt.Id(), valid with { Lines = [new ReturnLineBody(line, quantity)] });
            Assert.Equal("Line 1", await response.FieldAsync(HttpStatusCode.BadRequest, "MSG59"));
        }

        // On the receipt date itself the return is accepted.
        var accepted = await (await world.Owner.PostReturnAsync(receipt.Id(), valid with { ReturnDate = Today.AddDays(-5) })).ReadAsync(HttpStatusCode.Created);
        Assert.Single(accepted.GetProperty("returns").EnumerateArray());
    }

    [Fact]
    public async Task Only_a_completed_receipt_can_be_returned()
    {
        var world = await factory.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 10_000)));
        var cancelled = await world.CompletedAsync(0, 0, new LineBody(world.Other, 1, 10_000));
        await (await world.Owner.CancelAsync(cancelled.Id(), "Wrong", null)).ReadAsync(HttpStatusCode.OK);

        foreach (var receipt in new[] { draft, cancelled })
        {
            var problem = await (await world.Owner.PostReturnAsync(receipt.Id(), Return(receipt.LineOf(world.Other), 1, 0, 10_000))).ProblemAsync(HttpStatusCode.Conflict, "MSG61");
            Assert.EndsWith("only a Completed receipt can be returned.", problem.GetProperty("detail").GetString());
        }
    }

    [Fact]
    public async Task Returns_are_listed_with_the_receipt_and_read_one_by_one_with_audit_and_a_daily_code()
    {
        var (world, receipt, line) = await SeedReceiptAsync();
        var created = await (await world.Owner.PostReturnAsync(receipt.Id(), Return(line, 10, 90_000, 0))).ReadAsync(HttpStatusCode.Created);
        var listed = Assert.Single(created.GetProperty("returns").EnumerateArray());
        Assert.Matches($"^PRT-{Today:yyyyMMdd}-\\d{{3}}$", listed.GetProperty("code").GetString());

        var list = await (await world.Owner.GetAsync($"/api/purchase-receipts/{receipt.Id()}/returns")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(listed.Id(), Assert.Single(list.EnumerateArray()).Id());

        var detail = await (await world.Owner.GetAsync($"/api/purchase-returns/{listed.Id()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((receipt.Id(), receipt.GetProperty("code").GetString(), 90_000m, 90_000m, "Cash"),
            (detail.GetProperty("receiptId").GetGuid(), detail.GetProperty("receiptCode").GetString(), detail.Money("returnValue"), detail.Money("refundAmount"), detail.GetProperty("refundMethod").GetString()));
        var detailLine = Assert.Single(detail.GetProperty("lines").EnumerateArray());
        Assert.Equal((line, 10m, 90_000m, "kg"), (detailLine.GetProperty("receiptLineId").GetGuid(), detailLine.Money("quantity"), detailLine.Money("returnValue"), detailLine.GetProperty("unit").GetString()));
        await (await world.Owner.GetAsync($"/api/purchase-returns/{Guid.NewGuid()}")).ReadAsync(HttpStatusCode.NotFound);

        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == listed.Id()));
        Assert.Equal(("PurchaseReturn.Created", world.Zone.Id, "Weak seed"), (audit.Action, audit.ZoneId, audit.Reason));
        Assert.Contains("90000", audit.After);
        Assert.Contains(line.ToString(), audit.After);
    }

    [Fact]
    public async Task Returns_need_create_in_the_receipt_zone_and_are_not_readable_from_another_zone()
    {
        var (world, receipt, line) = await SeedReceiptAsync();
        var created = await (await world.Owner.PostReturnAsync(receipt.Id(), Return(line, 1, 0, 9000))).ReadAsync(HttpStatusCode.Created);
        var returnId = created.GetProperty("returns")[0].Id();
        var (otherZone, _) = await factory.ZoneWithRaftAsync();

        var otherManager = await factory.ZoneManagerAsync(otherZone);
        await (await otherManager.PostReturnAsync(receipt.Id(), Return(line, 1, 0, 9000))).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
        await (await otherManager.GetAsync($"/api/purchase-returns/{returnId}")).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
        var viewer = await factory.PurchasingStaffAsync(world.Zone, new Perm(PermissionFunctions.PurchaseReceipts));
        await (await viewer.PostReturnAsync(receipt.Id(), Return(line, 1, 0, 9000))).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
        await (await viewer.GetAsync($"/api/purchase-returns/{returnId}")).ReadAsync(HttpStatusCode.OK);
        var editorOnly = await factory.PurchasingStaffAsync(world.Zone, new Perm(PermissionFunctions.PurchaseReceipts, Edit: true, Delete: true));
        await (await editorOnly.PostReturnAsync(receipt.Id(), Return(line, 1, 0, 9000))).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
        Assert.Equal(1, await factory.QueryAsync(db => db.PurchaseReturns.CountAsync(r => r.ReceiptId == receipt.Id())));

        var manager = await factory.ZoneManagerAsync(world.Zone);
        await (await manager.PostReturnAsync(receipt.Id(), Return(line, 1, 0, 9000))).ReadAsync(HttpStatusCode.Created);
    }

    private async Task<decimal> DebtAsync(PurchasingWorld world) =>
        (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/suppliers/{world.SupplierId}/debt")).GetProperty("totals").Money("outstanding");
}
