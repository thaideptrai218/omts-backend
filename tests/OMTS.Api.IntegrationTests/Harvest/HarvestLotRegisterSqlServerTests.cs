using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Sales;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>The harvest lot list on SQL Server: its page, order and sums are worked out by SQL and must equal the figures the lot screens work out in code.</summary>
public class HarvestLotRegisterSqlServerTests
{
    [SqlServerFact]
    public async Task The_page_the_order_and_the_sums_of_the_list_equal_the_figures_worked_out_in_code()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        await owner.SeedingRecordAsync(cycleId, (await owner.SeedLotAsync()).Session([1, 2, 3, 4, 5]));
        var productId = (await owner.CreateProductAsync(unitPrice: 30_000m)).Id();

        // Five weighed lots and one still waiting for its weighing.
        var weighed = new List<Guid>();
        for (var cages = 2; cages <= 6; cages++)
        {
            weighed.Add((await owner.HarvestLotAsync(raftId, new HarvestFields { Cages = cages.ToString() })).Id());
        }

        var waiting = (await owner.RecordLotAsync(raftId, new HarvestFields { Cages = "7" })).Id();
        await host.UseDbAsync(db =>
        {
            // Lot 0: invoiced in part; lot 1: invoiced in part and a cancelled invoice that counts nowhere; lot 2: sold and returned back for sale; lot 3: sold and returned thrown away; lot 4: oversold, so 0 is left.
            db.SalesInvoices.AddRange(
                Invoice(host, zoneId, productId, (weighed[0], 10m)),
                Invoice(host, zoneId, productId, (weighed[1], 20m)),
                Invoice(host, zoneId, productId, StatusIds.SalesInvoice.Cancelled, (weighed[1], 500m)),
                Invoice(host, zoneId, productId, (weighed[4], 100_000m)));
            var returned = Invoice(host, zoneId, productId, (weighed[2], 30m));
            var thrownAway = Invoice(host, zoneId, productId, (weighed[3], 40m));
            db.SalesInvoices.AddRange(returned, thrownAway);
            db.SalesReturns.AddRange(
                Return(host, returned, weighed[2], 30m, SalesReturnOutcome.BackForSale, 0m, StatusIds.SalesReturn.Recorded),
                Return(host, thrownAway, weighed[3], 40m, SalesReturnOutcome.ThrownAway, 40m, StatusIds.SalesReturn.Recorded),
                Return(host, Invoice(host, zoneId, productId, (weighed[0], 5m)), weighed[0], 5m, SalesReturnOutcome.ThrownAway, 5m, StatusIds.SalesReturn.Cancelled));
        });
        var expected = await ExpectedAsync(host);
        Assert.Equal(6, expected.Count);

        foreach (var sort in new[] { "code", "harvestedAt", "remainingKg" })
        {
            foreach (var dir in new[] { "desc", "asc" })
            {
                var inOrder = Ordered(expected, sort, dir == "desc");

                var codes = new List<string>();
                JsonElement last = default;
                for (var page = 1; page <= 3; page++)
                {
                    last = await (await owner.GetAsync($"/api/harvest-lots?sort={sort}&sortDir={dir}&page={page}&pageSize=2")).ReadAsync(HttpStatusCode.OK);
                    foreach (var item in last.GetProperty("items").EnumerateArray())
                    {
                        codes.Add(item.GetProperty("lotCode").GetString()!);
                        var wanted = expected.Single(e => e.Row.Id == item.Id()).Figures;
                        var shown = item.GetProperty("figures");
                        Assert.Equal((wanted.SoldKg, wanted.RemainingKg, wanted.LostKg), (shown.Kg("soldKg"), shown.Kg("remainingKg"), shown.Kg("lostKg")));
                    }
                }

                Assert.Equal(inOrder.Select(e => e.Row.LotCode), codes);
                Assert.Equal(6, last.GetProperty("total").GetInt32());
                var totals = last.GetProperty("totals");
                Assert.Equal(
                    (expected.Sum(e => e.Row.CageCount), expected.Sum(e => e.Row.EstimatedKg), expected.Sum(e => e.Row.WeighedKg ?? 0m), expected.Sum(e => e.Figures.SoldKg), expected.Sum(e => e.Figures.RemainingKg), expected.Sum(e => e.Figures.LostKg)),
                    (totals.Kg("cageCount"), totals.Kg("estimatedKg"), totals.Kg("weighedKg"), totals.Kg("soldKg"), totals.Kg("remainingKg"), totals.Kg("lostKg")));
            }
        }

        Assert.Equal(0m, expected.Single(e => e.Row.Id == waiting).Figures.RemainingKg);
        Assert.Equal(0m, expected.Single(e => e.Row.Id == weighed[4]).Figures.RemainingKg);

        var filtered = await (await owner.GetAsync($"/api/harvest-lots?weighed=false&productId={Guid.NewGuid()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((0, 0m), (filtered.GetProperty("total").GetInt32(), filtered.GetProperty("totals").Kg("remainingKg")));

        var file = await owner.GetAsync("/api/harvest-lots/export?sort=remainingKg&sortDir=asc");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        using var book = new XLWorkbook(await file.Content.ReadAsStreamAsync());
        var sheet = book.Worksheet(1);
        Assert.Equal(
            expected.OrderBy(e => e.Figures.RemainingKg).ThenBy(e => e.Row.LotCode, StringComparer.Ordinal).Select(e => e.Row.LotCode),
            Enumerable.Range(2, 6).Select(row => sheet.Cell(row, 1).GetString()));
    }

    private static List<(HarvestLotListRow Row, HarvestLotFigures Figures)> Ordered(List<(HarvestLotListRow Row, HarvestLotFigures Figures)> lots, string sort, bool descending)
    {
        // The lot code, compared as plain characters, breaks every tie.
        var primary = sort switch
        {
            "harvestedAt" => descending ? lots.OrderByDescending(e => e.Row.HarvestedAt) : lots.OrderBy(e => e.Row.HarvestedAt),
            "remainingKg" => descending ? lots.OrderByDescending(e => e.Figures.RemainingKg) : lots.OrderBy(e => e.Figures.RemainingKg),
            _ => descending ? lots.OrderByDescending(e => e.Row.LotCode, StringComparer.Ordinal) : lots.OrderBy(e => e.Row.LotCode, StringComparer.Ordinal),
        };
        return (descending ? primary.ThenByDescending(e => e.Row.LotCode, StringComparer.Ordinal) : primary.ThenBy(e => e.Row.LotCode, StringComparer.Ordinal)).ToList();
    }

    private static async Task<List<(HarvestLotListRow Row, HarvestLotFigures Figures)>> ExpectedAsync(FarmingSqlServerHost host) =>
        await host.QueryAsync(async db =>
        {
            var rows = await db.HarvestLots.AsNoTracking()
                .Select(l => new HarvestLotListRow(
                    l.Id, l.LotCode, l.HarvestedAt, l.HarvestDate, l.Raft.ZoneId, l.Raft.Zone.Code, l.RaftId, l.Raft.Code, l.CycleId, l.CageCount, l.EstimatedKg,
                    l.WeighedKg, l.WeighedAt, l.StatusId, l.CloseReason, l.CloseDate, l.LeftoverKg, l.RecordedByAccountId))
                .ToListAsync();
            var invoiced = await db.SalesInvoiceLines.AsNoTracking().Where(l => l.Invoice.StatusId != StatusIds.SalesInvoice.Cancelled)
                .GroupBy(l => l.HarvestLotId).Select(g => new { g.Key, Kg = g.Sum(l => l.Kg) }).ToDictionaryAsync(x => x.Key, x => x.Kg);
            var returns = await db.SalesReturns.AsNoTracking().Where(SalesReturn.Standing).SelectMany(r => r.Lines)
                .GroupBy(l => l.HarvestLotId).Select(g => new { g.Key, Returned = g.Sum(l => l.ReturnedKg), Discarded = g.Sum(l => l.DiscardedKg) }).ToDictionaryAsync(x => x.Key);
            return rows.Select(r =>
            {
                var back = returns.GetValueOrDefault(r.Id);
                var sales = new LotSalesFigures(invoiced.GetValueOrDefault(r.Id), back?.Returned ?? 0m, (back?.Returned ?? 0m) - (back?.Discarded ?? 0m), back?.Discarded ?? 0m);
                return (r, HarvestLotFigures.Shown(r.StatusId, r.EstimatedKg, r.WeighedKg, r.LeftoverKg, sales));
            }).ToList();
        });

    private static SalesInvoice Invoice(FarmingSqlServerHost host, Guid zoneId, Guid productId, params (Guid LotId, decimal Kg)[] lines) =>
        Invoice(host, zoneId, productId, StatusIds.SalesInvoice.Completed, lines);

    private static SalesInvoice Invoice(FarmingSqlServerHost host, Guid zoneId, Guid productId, int statusId, params (Guid LotId, decimal Kg)[] lines) => new()
    {
        Code = "HD" + AcsTestKit.NewCode(), InvoicedAt = DateTimeOffset.UtcNow, InvoiceDate = Today, ZoneId = zoneId, Subtotal = 1_000m, Total = 1_000m, PaymentTerm = SalesPaymentTerm.PayNow,
        CreatedByAccountId = host.Owner.Account.Id, StatusId = statusId,
        Lines = [.. lines.Select((l, i) => new SalesInvoiceLine { LineNumber = i + 1, ProductId = productId, HarvestLotId = l.LotId, Kg = l.Kg, Quantity = l.Kg, UnitPrice = 1_000m, ListPrice = 1_000m, Amount = 1_000m })],
    };

    private static SalesReturn Return(FarmingSqlServerHost host, SalesInvoice invoice, Guid lotId, decimal kg, SalesReturnOutcome outcome, decimal discarded, int statusId) => new()
    {
        Code = "TL" + AcsTestKit.NewCode(), Invoice = invoice, ReturnDate = Today, Reason = "Test", Value = 1_000m, RecordedByAccountId = host.Owner.Account.Id, StatusId = statusId,
        CancelledAt = statusId == StatusIds.SalesReturn.Cancelled ? DateTimeOffset.UtcNow : null, CancelReason = statusId == StatusIds.SalesReturn.Cancelled ? "Mistake" : null,
        CancelledByAccountId = statusId == StatusIds.SalesReturn.Cancelled ? host.Owner.Account.Id : null,
        Lines = [new SalesReturnLine { InvoiceLineId = invoice.Lines.First().Id, HarvestLotId = lotId, ReturnedKg = kg, Outcome = outcome, DiscardedKg = discarded }],
    };
}
