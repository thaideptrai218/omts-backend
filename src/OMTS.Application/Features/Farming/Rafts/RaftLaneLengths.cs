using OMTS.Application.Common.Results;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Farming.Rafts;

/// <summary>One change of the line length of a lane, for the audit log: null is the length of the raft.</summary>
internal sealed record LaneLengthChange(int Lane, decimal? Before, decimal? After);

/// <summary>Sets the line length of the lanes a raft save names, and refuses a raft shorter than a line a lane keeps (decision of 12/11/2026).</summary>
internal static class RaftLaneLengths
{
    /// <summary>
    /// Applies the lengths to the lanes In Use of the raft, whose own length (<see cref="Raft.LengthM"/>) is already the new one. A lane not In Use is refused (400 "Line length");
    /// a lane left with a line longer than the raft is refused too, naming the lanes (400 "Length", <c>lanes</c>), whether it was set now or the raft was made shorter.
    /// </summary>
    public static (AppError? Error, IReadOnlyList<LaneLengthChange> Changes) Apply(Raft raft, IReadOnlyList<LaneLengthInput> inputs)
    {
        var changes = new List<LaneLengthChange>();
        foreach (var input in inputs)
        {
            var lane = raft.Lanes.FirstOrDefault(l => l.LaneNumber == input.LaneNumber && l.StatusId == StatusIds.Lane.InUse);
            if (lane is null)
            {
                return (AppError.Invalid(RaftGeometry.LineLengthField, $"Lane {input.LaneNumber} is not in use on this raft."), []);
            }

            if (lane.LineLengthM != input.LineLengthM)
            {
                changes.Add(new LaneLengthChange(lane.LaneNumber, lane.LineLengthM, input.LineLengthM));
                lane.LineLengthM = input.LineLengthM;
            }
        }

        if (raft.LengthM is { } length)
        {
            var tooLong = raft.Lanes
                .Where(l => l.StatusId == StatusIds.Lane.InUse && l.LineLengthM > length)
                .Select(l => l.LaneNumber).Order().ToList();
            if (tooLong.Count > 0)
            {
                return (AppError.Invalid(RaftGeometry.LengthField, $"Lanes {string.Join(", ", tooLong)} have a line longer than {length:0.##} m.") with
                {
                    Details = new Dictionary<string, object?> { ["field"] = RaftGeometry.LengthField, ["hint"] = $"Lanes {string.Join(", ", tooLong)} have a line longer than {length:0.##} m.", ["lanes"] = tooLong },
                }, []);
            }
        }

        return (null, changes);
    }
}
