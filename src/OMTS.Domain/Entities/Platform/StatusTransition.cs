namespace OMTS.Domain.Entities.Platform;

/// <summary>
/// E52 – A permitted status transition (Table 3.4-2 and the two USAGE transitions). Any transition not
/// declared here is blocked (BR-30, MSG61). Fixed by the SRS; not editable in the system.
/// </summary>
public class StatusTransition
{
    public int Id { get; set; }

    public int FromStatusId { get; set; }
    public Status FromStatus { get; set; } = null!;

    public int ToStatusId { get; set; }
    public Status ToStatus { get; set; } = null!;

    public string UseCases { get; set; } = string.Empty;
    public string AllowedPerformers { get; set; } = string.Empty;

    /// <summary>Action, System, or System; time.</summary>
    public string TriggerEvent { get; set; } = string.Empty;

    public string? GuardCondition { get; set; }
    public bool ReasonRequired { get; set; }
}
