using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// E38 – Team: a crew or contractor team led by a crew leader (exposed as "teams" by the API, LAB-02). Team
/// piecework is paid on one team payslip to the crew leader; daily wages go to each member (BR-20).
/// </summary>
public class Contractor : AuditableEntity, IHasStatus
{
    /// <summary>Unique, case-insensitive.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional; the phone of the leader is shown when empty.</summary>
    public string? PhoneNumber { get; set; }
    public string? OtherContacts { get; set; }

    /// <summary>Crew leader; always a current member of the team.</summary>
    public Guid? LeaderWorkerId { get; set; }
    public SeasonalWorker? Leader { get; set; }

    public int StatusId { get; set; } = StatusIds.Usage.InUse;
    public Status Status { get; set; } = null!;

    /// <summary>Workers whose current team this is.</summary>
    public ICollection<SeasonalWorker> Members { get; set; } = [];
}
