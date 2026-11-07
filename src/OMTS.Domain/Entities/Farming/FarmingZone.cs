using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Domain.Entities.Farming;

/// <summary>E07 – Unit organizing rafts and the scope of zone-based access (BR-01).</summary>
public class FarmingZone : AuditableEntity, IHasStatus
{
    public Guid CooperativeId { get; set; }
    public Cooperative Cooperative { get; set; } = null!;

    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Location { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }

    /// <summary>Area in hectares.</summary>
    public decimal? Area { get; set; }

    /// <summary>Current water thresholds entered by the Cooperative Owner (BR-WATER-04); changes go to AuditEvent.</summary>
    public decimal? PhMin { get; set; }
    public decimal? PhMax { get; set; }
    public decimal? SalinityMin { get; set; }
    public decimal? SalinityMax { get; set; }
    public decimal? TemperatureMin { get; set; }
    public decimal? TemperatureMax { get; set; }

    /// <summary>
    /// The start of the business day the overdue water measurement reminder (MSG-WATER-06) was last sent: the next one
    /// waits a month from that day (GFR-04).
    /// </summary>
    public DateTimeOffset? WaterOverdueRemindedAt { get; set; }

    public int StatusId { get; set; } = StatusIds.Usage.InUse;
    public Status Status { get; set; } = null!;

    public ICollection<Raft> Rafts { get; set; } = [];
    /// <summary>The zone licenses (FARM-03); each license belongs to one zone.</summary>
    public ICollection<ZoneComplianceDocument> Licenses { get; set; } = [];
    public ICollection<WaterMeasurement> WaterMeasurements { get; set; } = [];
}
