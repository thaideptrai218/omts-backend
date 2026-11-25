using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Purchasing;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>The material cost of a raft on SQL Server: the total worked out by SQL is the sum of the amounts of its usages, each costed and rounded half up to the dong.</summary>
public class RaftMaterialCostSqlServerTests
{
    [SqlServerFact]
    public async Task The_total_equals_the_rounded_amounts_of_the_usages_with_and_without_an_issue()
    {
        var (host, owner, zoneId, item) = await MaterialUsageRaceTests.StartAsync();
        await using var _ = host;
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var emptyRaftId = (await owner.CreateRaftAsync(zoneId)).Id();
        await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-20)));
        var usages = new List<Guid>();
        foreach (var (days, quantity) in new[] { (1, 1.125m), (2, 2.5m), (3, 12.5m), (4, 3m), (5, 0.333m) })
        {
            usages.Add((await owner.RecordUsageAsync(raftId, item, Today.AddDays(-days), quantity)).Id());
        }

        await host.UseDbAsync(db =>
        {
            // Two usages cost what their issue line says (10,001 per unit of 12 base units: a cost with many decimals per base unit); the others take the ledger.
            foreach (var (usageId, index) in usages.Take(2).Select((id, i) => (id, i)))
            {
                var issue = new InputIssue { Code = $"XD9{index}", Kind = InputIssueKind.Use, IssuedAt = DateTimeOffset.UtcNow, ZoneId = zoneId, RaftId = raftId, StatusId = StatusIds.InputIssue.Completed, CompletedAt = DateTimeOffset.UtcNow };
                issue.Lines.Add(new InputIssueLine { LineNumber = 1, ItemId = item, UnitFactor = 12m, Unit = "Box", EnteredQuantity = 1m, Quantity = 12m, UnitCost = 10_001m, Value = 10_001m });
                db.InputIssues.Add(issue);
                db.MaterialUsages.Single(u => u.Id == usageId).IssueId = issue.Id;
            }

            foreach (var (days, cost) in new[] { (30, 800m), (4, 45_501.5m), (3, 33.3333m) })
            {
                db.InputStockMovements.Add(new InputStockMovement
                {
                    ItemId = item, Kind = InputStockMovementKind.Receipt, DocumentCode = "PN9", OccurredAt = DateTimeOffset.UtcNow.AddDays(-days), Quantity = 100m, UnitCost = cost,
                });
            }
        });

        await using var scope = host.Api.Services.CreateAsyncScope();
        var costs = scope.ServiceProvider.GetRequiredService<IRaftMaterialCostQuery>();
        var rows = await costs.UsagesAsync(new RaftUsageCostFilter(RaftIds: [raftId]), default);
        var expected = rows.Sum(r => PurchaseMoney.LineAmount(r.Quantity, r.UnitPrice));

        var totals = await costs.GetTotalsAsync([raftId, emptyRaftId], default);

        Assert.Equal(5, rows.Count);
        Assert.True(expected > 0m);
        Assert.Equal((expected, 0m), (totals[raftId], totals[emptyRaftId]));
    }
}
