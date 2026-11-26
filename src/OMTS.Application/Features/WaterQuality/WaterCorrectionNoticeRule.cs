namespace OMTS.Application.Features.WaterQuality;

/// <summary>What WATER-02.2 sends after a void.</summary>
public enum WaterCorrectionNotice
{
    None,

    /// <summary>MSG-WATER-04.</summary>
    AlertWithdrawn,
}

/// <summary>
/// WATER-02.2 and BR-WATER-05: after a void something is sent only when the voided measurement was the latest active one of its
/// zone and had a value outside its thresholds: that alert is withdrawn. A voided measurement counts as having no value outside.
/// </summary>
public static class WaterCorrectionNoticeRule
{
    public static WaterCorrectionNotice AfterVoid(bool wasLatest, bool wasOutside) =>
        wasLatest && wasOutside ? WaterCorrectionNotice.AlertWithdrawn : WaterCorrectionNotice.None;
}
