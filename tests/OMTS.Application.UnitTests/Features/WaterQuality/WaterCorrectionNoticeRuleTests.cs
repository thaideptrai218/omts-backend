using OMTS.Application.Features.WaterQuality;

namespace OMTS.Application.UnitTests.Features.WaterQuality;

/// <summary>What WATER-02.2 sends after a void, without a database.</summary>
public sealed class WaterCorrectionNoticeRuleTests
{
    [Theory]
    [InlineData(true, true, WaterCorrectionNotice.AlertWithdrawn)]
    [InlineData(true, false, WaterCorrectionNotice.None)]
    [InlineData(false, true, WaterCorrectionNotice.None)]
    [InlineData(false, false, WaterCorrectionNotice.None)]
    public void A_void_withdraws_only_the_alert_of_the_latest(bool wasLatest, bool wasOutside, WaterCorrectionNotice expected)
    {
        Assert.Equal(expected, WaterCorrectionNoticeRule.AfterVoid(wasLatest, wasOutside));
    }
}
