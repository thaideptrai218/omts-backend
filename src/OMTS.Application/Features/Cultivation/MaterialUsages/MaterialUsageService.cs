using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Features.Cultivation.MaterialUsages;

/// <summary>
/// CYCLE-03.0 record material usage, for a Zone Manager of the raft's zone and the Cooperative Owner, while the raft is In Use: one repair makes one record per item. It notifies
/// nobody. The date may fall in an Open cycle or in no cycle, never in a closed cycle (BR-CYCLE-03).
/// </summary>
public sealed class MaterialUsageService(
    IMaterialUsageRepository usages,
    IRaftRepository rafts,
    IFarmingInputItemRepository items,
    MaterialUsageCycleGuard cycleGuard,
    RaftViews raftViews,
    MaterialUsageViews views,
    ZoneAccessGuard zoneAccess,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public async Task<Result<MaterialUsageRecorded>> RecordAsync(Guid raftId, RecordMaterialUsageRequest request, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, PermissionAction.Create, raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (raftViews.EditRefusal(raft) is { } refused)
        {
            return refused;
        }

        var lines = request.Items ?? [];
        // A JSON body can hold a null line, which CheckLines refuses.
        var itemIds = lines.Select(l => l?.ItemId).OfType<Guid>().Distinct().ToList();
        var found = (await items.GetByIdsAsync(itemIds, cancellationToken)).ToDictionary(i => i.Id);
        var check = new FieldCheck();
        MaterialUsageValues.CheckDate(check, request.UsageDate, timeProvider.Today());
        var repairReason = MaterialUsageValues.CheckRepairReason(check, request.RepairReason);
        CheckLines(check, lines, found);
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        if (DiscontinuedLine(lines, found) is { } discontinued)
        {
            return discontinued;
        }

        var date = request.UsageDate!.Value;
        if (await cycleGuard.HoldAsync(raft, date, cancellationToken) is { } closed)
        {
            return closed;
        }

        var recorder = currentUser.AccountId ?? throw new InvalidOperationException("Material usage needs an authenticated caller.");
        var saved = new List<MaterialUsage>(lines.Count);
        foreach (var line in lines)
        {
            var item = found[line.ItemId!.Value];
            var usage = new MaterialUsage
            {
                RaftId = raft.Id, UsageDate = date, RepairReason = repairReason, ItemId = item.Id, Quantity = line.Quantity!.Value, RecordedByAccountId = recorder,
            };
            usages.Add(usage);
            audit.Record(MaterialUsageValues.Entry(MaterialUsageValues.Created, usage, raft) with { After = MaterialUsageValues.Snapshot(usage, raft, item) });
            saved.Add(usage);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        var message = UserMessages.Saved("Material usage");
        return new MaterialUsageRecorded(await views.RecordsAsync(raft, saved.Select(u => u.Id).ToHashSet(), cancellationToken), message.Text, message.Code);
    }

    /// <summary>At least one line, each item of the group Material and only once, as a raft has one record per item of a repair (CYCLE-03 POST-1).</summary>
    private static void CheckLines(FieldCheck check, IReadOnlyList<MaterialUsageLine> lines, Dictionary<Guid, FarmingInputItem> found)
    {
        check.Check(lines.Count > 0, MaterialUsageValues.ItemsField);
        var seen = new HashSet<Guid>();
        for (var i = 0; i < lines.Count; i++)
        {
            var isMaterial = lines[i]?.ItemId is { } itemId && MaterialUsageValues.IsMaterial(found.GetValueOrDefault(itemId)) && seen.Add(itemId);
            check.Check(isMaterial, MaterialUsageValues.LineItemField(i));
            MaterialUsageValues.CheckQuantity(check, lines[i]?.Quantity, MaterialUsageValues.LineQuantityField(i));
        }
    }

    /// <summary>MSG-GEN-09 naming the first item that can no longer be chosen (GBR-01), which the list of items does not offer.</summary>
    private AppError? DiscontinuedLine(IReadOnlyList<MaterialUsageLine> lines, Dictionary<Guid, FarmingInputItem> found)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var item = found[lines[i].ItemId!.Value];
            if (MaterialUsageValues.IsDiscontinued(item))
            {
                return AppError.NotUsableChoice($"Input item {item.Code}", statusCatalog.DisplayNameOf(item.StatusId), MaterialUsageValues.LineItemField(i));
            }
        }

        return null;
    }
}
