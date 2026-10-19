namespace OMTS.Domain.Entities.Farming;

/// <summary>E09 – Links a compliance document to a zone it covers. Composite key (DocumentId, ZoneId).</summary>
public class DocumentZoneScope
{
    public Guid DocumentId { get; set; }
    public ZoneComplianceDocument Document { get; set; } = null!;

    public Guid ZoneId { get; set; }
    public FarmingZone Zone { get; set; } = null!;

    public DateOnly? ValidFrom { get; set; }
    public DateOnly? ValidTo { get; set; }
}
