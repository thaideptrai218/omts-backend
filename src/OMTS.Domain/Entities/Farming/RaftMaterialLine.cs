using OMTS.Domain.Common;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities;

/// <summary>E50 – One material line of a raft material record; quantity &gt; 0, unit price ≥ 0 (BR-27).</summary>
public class RaftMaterialLine : Entity
{
    public Guid RecordId { get; set; }
    public RaftMaterialRecord Record { get; set; } = null!;

    public MaterialType MaterialType { get; set; }

    /// <summary>Longitudinal line no. k (0…N), cross line sequence number, or float position note.</summary>
    public string? Position { get; set; }

    public string? Specification { get; set; }
    public int Quantity { get; set; }
    public MaterialUnit Unit { get; set; }
    public decimal UnitPrice { get; set; }

    /// <summary>VND, rounded to thousands.</summary>
    public decimal LineAmount { get; set; }
}
