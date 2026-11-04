using OMTS.Application.Common.Messages;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>What WATER-02.2 sends after a correction or a void.</summary>
public enum WaterCorrectionNotice
{
    None,

    /// <summary>MSG-WATER-03 marked "(corrected measurement)".</summary>
    CorrectedAlert,

    /// <summary>MSG-WATER-04.</summary>
    AlertWithdrawn,
}

/// <summary>
/// The matrix of WATER-02.2 and BR-WATER-05. Something is sent only for the latest active measurement of the zone, and
/// only when the change alters which of its indicators are outside its thresholds or on which side: a value that crosses
/// to the other side of its range counts as different (pH 6.0 to 9.0, PH:Low against PH:High), and a new reading on the same
/// side sends nothing (9.1 to 9.3). A voided measurement counts as having no value outside. "Latest" is judged at step 6
/// for a correction, so with the corrected time, and before the void for a void.
/// </summary>
public static class WaterCorrectionNoticeRule
{
    public static WaterCorrectionNotice AfterCorrection(bool isLatest, IReadOnlyCollection<WaterValueOutside> outsideBefore, IReadOnlyCollection<WaterValueOutside> outsideAfter)
    {
        if (!isLatest || Sides(outsideBefore).SetEquals(Sides(outsideAfter)))
        {
            return WaterCorrectionNotice.None;
        }

        return outsideAfter.Count > 0 ? WaterCorrectionNotice.CorrectedAlert : WaterCorrectionNotice.AlertWithdrawn;
    }

    public static WaterCorrectionNotice AfterVoid(bool wasLatest, bool wasOutside) =>
        wasLatest && wasOutside ? WaterCorrectionNotice.AlertWithdrawn : WaterCorrectionNotice.None;

    /// <summary>
    /// Whether the measurement, with its current time, is the latest active one of its zone (BR-WATER-04: by measurement
    /// time; then, as in the lists, by save time and id).
    /// </summary>
    /// <param name="latestStored">The two latest active measurements as stored, so at least one other than this one when there is one.</param>
    public static bool IsLatest(WaterMeasurement measurement, IReadOnlyList<WaterMeasurement> latestStored)
    {
        var other = latestStored.FirstOrDefault(m => m.Id != measurement.Id);
        if (other is null)
        {
            return true;
        }

        var order = measurement.MeasuredAt.CompareTo(other.MeasuredAt);
        order = order != 0 ? order : measurement.CreatedAt.CompareTo(other.CreatedAt);
        return (order != 0 ? order : measurement.Id.CompareTo(other.Id)) > 0;
    }

    private static HashSet<(string Indicator, WaterValueDirection Direction)> Sides(IEnumerable<WaterValueOutside> outside) =>
        outside.Select(value => (value.Indicator, value.Direction)).ToHashSet();
}
