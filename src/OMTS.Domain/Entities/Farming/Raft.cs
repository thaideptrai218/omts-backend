using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Domain.Entities.Farming;

/// <summary>
/// E10 – Physical structure grouping lanes. Its material cost is the total of the purchase receipts
/// linked to it (INP-03); float and line counts are not kept.
/// </summary>
public class Raft : AuditableEntity, IHasStatus
{
    public Guid ZoneId { get; set; }
    public FarmingZone Zone { get; set; } = null!;

    /// <summary>Unique within the zone.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Free-text location description.</summary>
    public string? Location { get; set; }

    /// <summary>Point picked on the map (WGS84); shown on maps and the traceability page via Google Maps.</summary>
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }

    public string? Size { get; set; }
    public string? PhotoUrl { get; set; }
    public string? ConditionNotes { get; set; }

    public int StatusId { get; set; } = StatusIds.Usage.InUse;
    public Status Status { get; set; } = null!;

    public ICollection<Lane> Lanes { get; set; } = [];
    public ICollection<RaftLayoutVersion> LayoutVersions { get; set; } = [];
    public ICollection<CultivationCycle> CultivationCycles { get; set; } = [];
    /// <summary>Purchase receipts of raft materials; their total is the raft material cost (FARM-04, INP-03).</summary>
    public ICollection<PurchaseReceipt> PurchaseReceipts { get; set; } = [];
}
