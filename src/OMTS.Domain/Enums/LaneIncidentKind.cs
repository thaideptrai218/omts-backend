namespace OMTS.Domain.Enums;

/// <summary>What happened to a lane (decision of 12/11/2026): it is reported damaged, repaired, or put out of service. There is no replace, no removal from the middle and no renumbering.</summary>
public enum LaneIncidentKind
{
    Damaged = 1,
    Repaired = 2,
    OutOfService = 3,
}
