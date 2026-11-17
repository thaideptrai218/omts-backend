using System.Globalization;
using System.Text.RegularExpressions;
using OMTS.Application.Features.Farming;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Farming.Rafts;

/// <summary>
/// The numeric geometry of a raft (decision of 12/11/2026): length along the lines, width across the lanes, the floor area worked out from them, and an optional line length per lane.
/// Pure functions. The legacy text <see cref="Raft.Size"/> is kept in step: written as "{length} m x {width} m" whenever the numbers are saved, and read back for rows saved before.
/// </summary>
public static partial class RaftGeometry
{
    public const decimal MinLengthM = 1m;
    public const decimal MaxLengthM = 500m;
    public const decimal MinWidthM = 1m;
    public const decimal MaxWidthM = 200m;
    public const decimal MaxAreaM2 = 100_000m;

    public const string LengthField = "Length";
    public const string WidthField = "Width";
    public const string LineLengthField = "Line length";

    /// <summary>Warning: each lane is narrower than 1 m across (width divided by the lanes in use).</summary>
    public const string NarrowCode = "RAFT_NARROW_FOR_LANES";

    /// <summary>Warning: the lanes in use do not all have the same line length.</summary>
    public const string LaneLengthsDifferCode = "RAFT_LANE_LENGTHS_DIFFER";

    [GeneratedRegex(@"^\s*(\d+(?:[.,]\d{1,2})?)\s*m\s*[x×X]\s*(\d+(?:[.,]\d{1,2})?)\s*m\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SizeText();

    /// <summary>At most 2 decimals, as the columns hold.</summary>
    public static bool HasAllowedScale(decimal value) => decimal.Round(value, 2) == value;

    public static decimal? AreaOf(decimal? length, decimal? width) =>
        length is { } l && width is { } w ? decimal.Round(l * w, 2, MidpointRounding.AwayFromZero) : null;

    /// <summary>"24 m x 10 m": invariant decimal point, no trailing zeros, as the web wrote it before.</summary>
    public static string SizeOf(decimal length, decimal width) =>
        string.Create(CultureInfo.InvariantCulture, $"{length:0.##} m x {width:0.##} m");

    /// <summary>The length and width a legacy size text holds ("24 m x 10 m"), or null when it is not in that form or out of range: never guessed.</summary>
    public static (decimal Length, decimal Width)? Parse(string? size)
    {
        if (size is null || SizeText().Match(size) is not { Success: true } match)
        {
            return null;
        }

        var length = decimal.Parse(match.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        var width = decimal.Parse(match.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        return IsLengthInRange(length) && IsWidthInRange(width) ? (length, width) : null;
    }

    public static bool IsLengthInRange(decimal length) => length is >= MinLengthM and <= MaxLengthM && HasAllowedScale(length);

    public static bool IsWidthInRange(decimal width) => width is >= MinWidthM and <= MaxWidthM && HasAllowedScale(width);

    /// <summary>The length a lane's lines have: its own, else the raft's.</summary>
    public static decimal? EffectiveLineLength(Lane lane, Raft raft) => lane.LineLengthM ?? raft.LengthM;

    /// <summary>Whether a lane's own line length is valid for a raft of this length: 1 m up to the length of the raft.</summary>
    public static bool IsLineLengthValid(decimal lineLength, decimal raftLength) => lineLength >= 1m && lineLength <= raftLength && HasAllowedScale(lineLength);

    /// <summary>Two lines per lane: the metres of line the lanes in use need, an aid when buying line. Null when a length is unknown.</summary>
    public static decimal? TotalLineLength(Raft raft)
    {
        var lengths = raft.Lanes.Where(l => l.StatusId == OMTS.Domain.Constants.StatusIds.Lane.InUse).Select(l => EffectiveLineLength(l, raft)).ToList();
        return lengths.Count == 0 || lengths.Any(l => l is null) ? null : 2m * lengths.Sum(l => l!.Value);
    }

    /// <summary>The soft remarks on a saved raft: lanes narrower than 1 m across, and lanes of unequal length. Never blocks a save.</summary>
    public static IReadOnlyList<FarmingWarning> Warnings(Raft raft)
    {
        var warnings = new List<FarmingWarning>();
        var inUse = raft.Lanes.Where(l => l.StatusId == OMTS.Domain.Constants.StatusIds.Lane.InUse).ToList();
        if (raft.WidthM is { } width && inUse.Count > 0 && width / inUse.Count < 1m)
        {
            var each = decimal.Round(width / inUse.Count, 2);
            warnings.Add(new FarmingWarning(
                NarrowCode, $"Each lane is only {each:0.##} m wide across. Check the width of the raft.",
                new Dictionary<string, object?> { ["widthPerLaneM"] = each, ["laneCount"] = inUse.Count }));
        }

        if (inUse.Select(l => EffectiveLineLength(l, raft)).Distinct().Count() > 1)
        {
            warnings.Add(new FarmingWarning(LaneLengthsDifferCode, "The lanes in use do not all have the same line length."));
        }

        return warnings;
    }
}
