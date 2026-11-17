using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Domain.Entities.Farming;

/// <summary>
/// E10 – Physical structure grouping lanes. Its material cost is the material used on it, each at the latest purchase
/// price of the item (BR-FARM-05); float and line counts are not kept.
/// </summary>
public class Raft : AuditableEntity, IHasStatus
{
    /// <summary>The most photos a raft may hold (FARM-04).</summary>
    public const int MaxPhotos = 3;

    public Guid ZoneId { get; set; }
    public FarmingZone Zone { get; set; } = null!;

    /// <summary>Unique within the zone.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Free-text location description.</summary>
    public string? Location { get; set; }

    /// <summary>Optional point picked on the map (WGS84); shown on maps and the traceability page via Google Maps.</summary>
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }

    /// <summary>
    /// The legacy text "{length} m x {width} m". The server writes it again whenever the geometry below is saved, so older clients and reports still read it; for a raft that
    /// has no numeric geometry yet it stays as it was typed.
    /// </summary>
    public string? Size { get; set; }

    /// <summary>Length of the raft in metres, along the lines: the longest line any lane may have. Null only for a raft whose old size text could not be read.</summary>
    public decimal? LengthM { get; set; }

    /// <summary>Width of the raft in metres, across all lanes. Null as <see cref="LengthM"/>.</summary>
    public decimal? WidthM { get; set; }

    /// <summary>Floor area in square metres, length times width rounded to 2 decimals; worked out, never stored or sent by a client. Null when a side is unknown.</summary>
    public decimal? AreaM2 => LengthM is { } length && WidthM is { } width ? decimal.Round(length * width, 2, MidpointRounding.AwayFromZero) : null;
    public string? ConditionNotes { get; set; }

    public int StatusId { get; set; } = StatusIds.Usage.InUse;
    public Status Status { get; set; } = null!;

    public ICollection<Lane> Lanes { get; set; } = [];
    public ICollection<RaftPhoto> Photos { get; set; } = [];
    public ICollection<CultivationCycle> CultivationCycles { get; set; } = [];
    /// <summary>Purchase receipt lines that name the raft (optional, kept from older receipts); they are not its material cost.</summary>
    public ICollection<PurchaseReceiptLine> PurchaseReceiptLines { get; set; } = [];
}
