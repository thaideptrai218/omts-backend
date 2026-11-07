using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest;

/// <summary>The audit rows of a lot (GBR-02): the record code is the lot code. The values hold lane numbers and storage keys, as a reader of the log knows them.</summary>
public static class HarvestLotAudit
{
    public const string Created = "HarvestLot.Created";
    public const string Closed = "HarvestLot.Closed";
    public const string Reopened = "HarvestLot.Reopened";
    public const string RecordType = nameof(HarvestLot);

    /// <param name="zoneId">The zone of the raft of the lot.</param>
    internal static AuditEntry Entry(string action, HarvestLot lot, Guid zoneId) => new(action, RecordType)
    {
        RecordId = lot.Id, RecordCode = lot.LotCode, ZoneId = zoneId,
    };

    /// <summary>The lot with its lanes.</summary>
    internal static object Snapshot(HarvestLot lot) => new
    {
        lot.LotCode,
        lot.HarvestedAt,
        lot.HarvestDate,
        Lanes = lot.Lanes.Select(l => l.Lane.LaneNumber).Order().ToList(),
        FinishedLanes = lot.Lanes.Where(l => l.IsFinished).Select(l => l.Lane.LaneNumber).Order().ToList(),
        lot.CageCount,
        lot.KgPerCage,
        lot.EstimatedKg,
        Photos = lot.Photos.OrderBy(p => p.SortOrder).Select(p => p.FileKey).ToList(),
    };

    /// <summary>The close facts of the lot, before and after a close or reopen.</summary>
    internal static object CloseFacts(HarvestLot lot, string status) => new
    {
        Status = status, lot.CloseDate, lot.LeftoverKg, lot.CloseReason, lot.ClosedBySalesInvoiceId, lot.ReopenedOn,
    };
}
