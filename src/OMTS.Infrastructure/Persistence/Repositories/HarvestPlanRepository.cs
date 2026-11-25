using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class HarvestPlanRepository(OmtsDbContext db) : IHarvestPlanRepository
{
    private static readonly int[] OpenStatuses = [StatusIds.HarvestPlan.Draft, StatusIds.HarvestPlan.Assigned, StatusIds.HarvestPlan.Received, StatusIds.HarvestPlan.Harvesting, StatusIds.HarvestPlan.HandedOver];

    public Task<HarvestPlan?> GetForEditAsync(Guid planId, CancellationToken cancellationToken) =>
        WithAll(db.HarvestPlans).FirstOrDefaultAsync(p => p.Id == planId, cancellationToken);

    public Task<HarvestPlan?> GetByLineForEditAsync(Guid lineId, CancellationToken cancellationToken) =>
        WithAll(db.HarvestPlans).FirstOrDefaultAsync(p => p.Lines.Any(l => l.Id == lineId), cancellationToken);

    public Task<HarvestPlan?> FindByCodeAsync(string code, Guid? exceptPlanId, CancellationToken cancellationToken)
    {
        return db.HarvestPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Code == code && p.Id != exceptPlanId, cancellationToken);
    }

    public async Task<(IReadOnlyList<HarvestPlan> Items, int Total)> SearchAsync(HarvestPlanFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = Filtered(filter);
        var total = await query.CountAsync(cancellationToken);
        var ids = await query.OrderByDescending(p => p.Code).ThenBy(p => p.Id).Skip((page - 1) * pageSize).Take(pageSize).Select(p => p.Id).ToListAsync(cancellationToken);
        if (ids.Count == 0)
        {
            return ([], total);
        }

        var plans = (await WithAll(db.HarvestPlans.AsNoTracking()).Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken)).ToDictionary(p => p.Id);
        return (ids.Select(id => plans[id]).ToList(), total);
    }

    public async Task<IReadOnlyList<HarvestPlan>> ListInProgressAsync(IReadOnlySet<Guid>? zoneIds, CancellationToken cancellationToken)
    {
        var query = db.HarvestPlans.AsNoTracking().Where(p => p.StatusId == StatusIds.HarvestPlan.Assigned || p.StatusId == StatusIds.HarvestPlan.Received || p.StatusId == StatusIds.HarvestPlan.Harvesting);
        if (zoneIds is not null)
        {
            var zones = zoneIds.ToList();
            query = query.Where(p => p.Lines.Any(l => zones.Contains(l.Raft.ZoneId)));
        }

        var ids = await query.Select(p => p.Id).ToListAsync(cancellationToken);
        return ids.Count == 0
            ? []
            : (await WithAll(db.HarvestPlans.AsNoTracking()).Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken)).OrderBy(p => p.PlannedAt).ThenBy(p => p.Code).ToList();
    }

    public async Task<IReadOnlyList<OpenLaneClaim>> OpenLaneClaimsAsync(IReadOnlyCollection<Guid> raftIds, Guid? exceptPlanId, CancellationToken cancellationToken)
    {
        var rafts = raftIds.Distinct().ToList();
        if (rafts.Count == 0)
        {
            return [];
        }

        return await db.HarvestPlanLanes.AsNoTracking()
            .Where(l => l.SkippedAt == null
                && rafts.Contains(l.Line.RaftId)
                && !db.HarvestLots.Any(lot => lot.HarvestPlanLineId == l.LineId)
                && OpenStatuses.Contains(l.Line.Plan.StatusId)
                && l.Line.PlanId != exceptPlanId)
            .Select(l => new OpenLaneClaim(l.Line.PlanId, l.Line.Plan.Code, l.Line.RaftId, l.Line.Raft.Code, l.LaneId, l.Lane.LaneNumber))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, Guid>> CycleProductsAsync(IReadOnlyCollection<Guid> raftIds, CancellationToken cancellationToken)
    {
        var rafts = raftIds.Distinct().ToList();
        if (rafts.Count == 0)
        {
            return new Dictionary<Guid, Guid>();
        }

        // A raft holds at most one Open cycle (BR-CYCLE-01), so each raft has at most one row.
        var rows = await db.CultivationCycles.AsNoTracking()
            .Where(c => rafts.Contains(c.RaftId) && c.StatusId == StatusIds.CultivationCycle.Open && c.ProductId != null)
            .Select(c => new { c.RaftId, ProductId = c.ProductId!.Value })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.RaftId, r => r.ProductId);
    }

    public async Task<IReadOnlyDictionary<Guid, CycleMaturity>> CycleMaturityAsync(IReadOnlyCollection<Guid> raftIds, CancellationToken cancellationToken)
    {
        var rafts = raftIds.Distinct().ToList();
        if (rafts.Count == 0)
        {
            return new Dictionary<Guid, CycleMaturity>();
        }

        // The seed of a cycle is the seed item of the line of its nursery batch.
        var rows = await db.CultivationCycles.AsNoTracking()
            .Where(c => rafts.Contains(c.RaftId) && c.StatusId == StatusIds.CultivationCycle.Open)
            .Select(c => new
            {
                c.RaftId,
                c.StartDate,
                c.ProductId,
                BatchDays = db.NurseryBatches.Where(b => b.Id == c.NurseryBatchId).Select(b => b.SeedLotLine.Item!.MaturityDays).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        // A cycle with no batch (or a batch whose seed gives no days) takes the days of a seed item that sells the cycle's product.
        var productIds = rows.Where(r => r.BatchDays is null && r.ProductId is not null).Select(r => r.ProductId!.Value).Distinct().ToList();
        var byProduct = productIds.Count == 0
            ? new Dictionary<Guid, int>()
            : (await db.FarmingInputItems.AsNoTracking()
                .Where(i => i.Group == OMTS.Domain.Enums.InputItemGroup.Seed && !i.IsHidden && i.ProductId != null && productIds.Contains(i.ProductId.Value) && i.MaturityDays != null)
                .Select(i => new { ProductId = i.ProductId!.Value, Days = i.MaturityDays!.Value })
                .ToListAsync(cancellationToken))
                .GroupBy(i => i.ProductId).ToDictionary(g => g.Key, g => g.Min(i => i.Days));
        return rows.ToDictionary(
            r => r.RaftId,
            r => new CycleMaturity(r.StartDate, r.BatchDays ?? (r.ProductId is { } p && byProduct.TryGetValue(p, out var days) ? days : null)));
    }

    public async Task<IReadOnlyList<PlanOfLot>> PlansOfLotsAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken)
    {
        var ids = lotIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await db.HarvestLots.AsNoTracking()
            .Where(l => ids.Contains(l.Id) && l.HarvestPlanLineId != null)
            .Select(l => new { LotId = l.Id, PlanId = l.HarvestPlanLine!.PlanId, l.HarvestPlanLine.Plan.Code })
            .ToListAsync(cancellationToken);
        var planIds = rows.Select(r => r.PlanId).Distinct().ToList();
        var links = await db.SalesOrderHarvestPlans.AsNoTracking().Where(o => planIds.Contains(o.PlanId)).Select(o => new { o.PlanId, o.OrderId }).ToListAsync(cancellationToken);
        return rows.Select(r => new PlanOfLot(r.LotId, r.PlanId, r.Code, links.Where(o => o.PlanId == r.PlanId).Select(o => o.OrderId).ToList())).ToList();
    }

    public async Task<PlannedLanes?> PlannedLanesOfLineAsync(Guid lineId, CancellationToken cancellationToken)
    {
        var rows = await db.HarvestPlanLanes.AsNoTracking().Where(l => l.LineId == lineId).Select(l => new { l.LaneId, l.Line.Plan.Code }).ToListAsync(cancellationToken);
        if (rows.Count > 0)
        {
            return new PlannedLanes(rows[0].Code, rows.Select(r => r.LaneId).ToHashSet());
        }

        // A line planned without lanes holds no lane check: the lot may hold any lane of the raft.
        return null;
    }

    public async Task<IReadOnlyList<PlanOfOrder>> PlansOfOrdersAsync(IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken)
    {
        var ids = orderIds.Distinct().ToList();
        return ids.Count == 0
            ? []
            : await db.SalesOrderHarvestPlans.AsNoTracking()
                .Where(o => ids.Contains(o.OrderId))
                .OrderBy(o => o.Plan.Code)
                .Select(o => new PlanOfOrder(o.OrderId, o.PlanId, o.Plan.Code, o.Plan.StatusId, o.Plan.PlannedAt, o.Plan.AssignedAt != null))
                .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SalesOrder>> OrdersByIdsAsync(IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken)
    {
        var ids = orderIds.Distinct().ToList();
        return ids.Count == 0 ? [] : await db.SalesOrders.AsNoTracking().Include(o => o.Customer).Where(o => ids.Contains(o.Id)).ToListAsync(cancellationToken);
    }

    public void Add(HarvestPlan plan) => db.HarvestPlans.Add(plan);

    public void AddLine(HarvestPlanLine line) => db.HarvestPlanLines.Add(line);

    public void RemoveLine(HarvestPlanLine line) => db.HarvestPlanLines.Remove(line);

    public void AddLane(HarvestPlanLane lane) => db.HarvestPlanLanes.Add(lane);

    public void RemoveLane(HarvestPlanLane lane) => db.HarvestPlanLanes.Remove(lane);

    public void AddOrderLink(SalesOrderHarvestPlan link) => db.SalesOrderHarvestPlans.Add(link);

    public void RemoveOrderLink(SalesOrderHarvestPlan link) => db.SalesOrderHarvestPlans.Remove(link);

    private IQueryable<HarvestPlan> Filtered(HarvestPlanFilter filter)
    {
        var plans = db.HarvestPlans.AsNoTracking().AsQueryable();
        if (filter.ZoneIds is { } reach)
        {
            var zones = reach.ToList();
            plans = plans.Where(p => p.Lines.Any(l => zones.Contains(l.Raft.ZoneId)));
        }

        if (filter.AssignedOnly)
        {
            plans = plans.Where(p => p.AssignedAt != null);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            plans = plans.Where(p => p.Code.Contains(text)
                || p.Lines.Any(l => l.Raft.Code.Contains(text))
                || p.Orders.Any(o => o.Order.Code.Contains(text)));
        }

        if (filter.StatusIds is { Count: > 0 } statusIds)
        {
            plans = plans.Where(p => statusIds.Contains(p.StatusId));
        }

        if (filter.From is { } from)
        {
            plans = plans.Where(p => p.PlannedDate >= from);
        }

        if (filter.To is { } to)
        {
            plans = plans.Where(p => p.PlannedDate <= to);
        }

        if (filter.ZoneId is { } zoneId)
        {
            plans = plans.Where(p => p.Lines.Any(l => l.Raft.ZoneId == zoneId));
        }

        if (filter.SalesOrderId is { } orderId)
        {
            plans = plans.Where(p => p.Orders.Any(o => o.OrderId == orderId));
        }

        return plans;
    }

    private static IQueryable<HarvestPlan> WithAll(IQueryable<HarvestPlan> plans) =>
        plans.Include(p => p.Lines).ThenInclude(l => l.Raft).ThenInclude(r => r.Zone)
            .Include(p => p.Lines).ThenInclude(l => l.Lanes).ThenInclude(l => l.Lane)
            .Include(p => p.Lines).ThenInclude(l => l.ExpectedProduct)
            .Include(p => p.Lines).ThenInclude(l => l.Lot).ThenInclude(l => l!.Lanes)
            .Include(p => p.Orders).ThenInclude(o => o.Order).ThenInclude(o => o.Customer)
            .AsSplitQuery();
}
