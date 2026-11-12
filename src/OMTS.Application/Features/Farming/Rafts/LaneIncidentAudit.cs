using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Farming.Rafts;

/// <summary>The audit rows of the lane commands (GBR-02): the record is the lane, its code is "{raft code}/{lane number}", and the reason is the one the user gave.</summary>
public static class LaneIncidentAudit
{
    public const string RecordType = nameof(Lane);
    public const string Damaged = "Lane.Damaged";
    public const string Repaired = "Lane.Repaired";
    public const string OutOfService = "Lane.OutOfService";
    public const string Restored = "Lane.Restored";

    internal static AuditEntry Entry(string action, Raft raft, Lane lane, string reason) => new(action, RecordType)
    {
        RecordId = lane.Id, RecordCode = $"{raft.Code}/{lane.LaneNumber}", ZoneId = raft.ZoneId, Reason = reason,
    };

    /// <summary>The values of a lane the commands change, for the before and after of an entry.</summary>
    internal static object Snapshot(Lane lane, string state) => new { lane.IsDamaged, IsOutOfService = lane.IsOutOfService, lane.OutOfServiceOn, State = state };
}
