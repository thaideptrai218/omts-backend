using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Farming.Rafts;

/// <summary>
/// The checked fields of a raft create or edit (MSG-GEN-01 names the others), and the values the audit log keeps. <paramref name="Size"/> is the legacy text: written from the numbers when they
/// are sent, else the text as sent (and read back into the numbers when it has the form "24 m x 10 m").
/// </summary>
internal sealed record RaftValues(
    string Code,
    decimal? Latitude,
    decimal? Longitude,
    string? Location,
    string Size,
    string? ConditionNotes,
    int LaneCount,
    decimal? LengthM,
    decimal? WidthM,
    IReadOnlyList<LaneLengthInput> LaneLengths)
{
    public static FieldCheck Read(SaveRaftRequest request, out RaftValues values)
    {
        var check = new FieldCheck();
        var code = check.Required(request.Code, "Code", SequentialCodes.RaftCodeMaxLength);
        var (latitude, longitude) = check.Coordinates(request.Latitude, request.Longitude, required: false);
        var location = check.Optional(request.Location, "Location", 500);

        string size;
        decimal? length, width;
        if (request.LengthM is not null || request.WidthM is not null)
        {
            // The numbers are the truth: both are required and in range, and the legacy text follows them.
            check.Check(request.LengthM is { } l && RaftGeometry.IsLengthInRange(l), RaftGeometry.LengthField);
            check.Check(request.WidthM is { } w && RaftGeometry.IsWidthInRange(w), RaftGeometry.WidthField);
            (length, width) = (request.LengthM, request.WidthM);
            size = length is { } okLength && width is { } okWidth && RaftGeometry.IsLengthInRange(okLength) && RaftGeometry.IsWidthInRange(okWidth)
                ? RaftGeometry.SizeOf(okLength, okWidth)
                : string.Empty;
        }
        else
        {
            size = check.Required(request.Size, "Size", 100);
            var parsed = RaftGeometry.Parse(size);
            (length, width) = (parsed?.Length, parsed?.Width);
        }

        var notes = check.Optional(request.ConditionNotes, "Condition notes", 2000);
        check.Check(request.LaneCount is >= 1 and <= RaftLaneChange.MaxLaneCount, "Number of lanes");
        var laneLengths = ReadLaneLengths(request.LaneLengths, length, check);
        values = new RaftValues(code, latitude, longitude, location, size, notes, request.LaneCount ?? 0, length, width, laneLengths);
        return check;
    }

    /// <summary>Each lane once, a number from 1, and a length of 1 m up to the length of the raft; a raft with no numeric length takes none.</summary>
    private static IReadOnlyList<LaneLengthInput> ReadLaneLengths(IReadOnlyList<LaneLengthInput>? input, decimal? raftLength, FieldCheck check)
    {
        if (input is null || input.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<int>();
        var accepted = new List<LaneLengthInput>(input.Count);
        foreach (var entry in input)
        {
            var valid = entry?.LaneNumber is >= 1 and <= RaftLaneChange.MaxLaneCount && seen.Add(entry.LaneNumber.Value)
                && (entry.LineLengthM is null || (raftLength is { } max && RaftGeometry.IsLineLengthValid(entry.LineLengthM.Value, max)));
            check.Check(valid, RaftGeometry.LineLengthField);
            if (valid)
            {
                accepted.Add(entry!);
            }
        }

        return accepted;
    }

    /// <summary>Everything but the lanes, which change through <see cref="RaftLaneChange"/>.</summary>
    public void ApplyTo(Raft raft)
    {
        raft.Code = Code;
        raft.Latitude = Latitude;
        raft.Longitude = Longitude;
        raft.Location = Location;
        raft.Size = Size;
        raft.LengthM = LengthM;
        raft.WidthM = WidthM;
        raft.ConditionNotes = ConditionNotes;
    }

    public static object Snapshot(Raft r) => new { r.Code, r.Latitude, r.Longitude, r.Location, r.Size, r.LengthM, r.WidthM, r.ConditionNotes };
}
