using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Farming.Rafts;

/// <summary>The checked fields of a raft create or edit (MSG-GEN-01 names the others), and the values the audit log keeps.</summary>
internal sealed record RaftValues(string Code, decimal? Latitude, decimal? Longitude, string? Location, string Size, string? ConditionNotes, int LaneCount)
{
    public static FieldCheck Read(SaveRaftRequest request, out RaftValues values)
    {
        var check = new FieldCheck();
        var code = check.Required(request.Code, "Code", SequentialCodes.RaftCodeMaxLength);
        check.Coordinates(request.Latitude, request.Longitude, required: false);
        var location = check.Optional(request.Location, "Location", 500);
        var size = check.Required(request.Size, "Size", 100);
        var notes = check.Optional(request.ConditionNotes, "Condition notes", 2000);
        check.Check(request.LaneCount is >= 1 and <= RaftLaneChange.MaxLaneCount, "Number of lanes");
        values = new RaftValues(code, request.Latitude, request.Longitude, location, size, notes, request.LaneCount ?? 0);
        return check;
    }

    /// <summary>Everything but the lanes, which change through <see cref="RaftLaneChange"/>.</summary>
    public void ApplyTo(Raft raft)
    {
        raft.Code = Code;
        raft.Latitude = Latitude;
        raft.Longitude = Longitude;
        raft.Location = Location;
        raft.Size = Size;
        raft.ConditionNotes = ConditionNotes;
    }

    public static object Snapshot(Raft r) => new { r.Code, r.Latitude, r.Longitude, r.Location, r.Size, r.ConditionNotes };
}
