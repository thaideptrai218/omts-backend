using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Farming.Rafts;

/// <summary>Why a lane cannot take seed or a harvest from its own flags (decision of 12/11/2026): the codes clients use to hide it from pickers and to say why.</summary>
public static class LaneReasonCodes
{
    public const string Damaged = "LaneDamaged";
    public const string OutOfService = "LaneOutOfService";

    /// <summary>Out of service first, as it is final; null for a lane with neither flag.</summary>
    public static string? Of(Lane lane) => lane.IsOutOfService ? OutOfService : lane.IsDamaged ? Damaged : null;

    public static string TextOf(string code) => code == OutOfService ? "The lane is out of service." : "The lane is damaged and waits for a repair.";
}
