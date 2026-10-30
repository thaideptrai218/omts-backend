using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Application.Features.WaterQuality;
using OMTS.Domain.Entities.Cultivation;
using S = OMTS.Domain.Constants.StatusIds.WaterMeasurement;

namespace OMTS.Application.UnitTests.Features.WaterQuality;

public class WaterMeasurementStatusesTests
{
    [Fact]
    public void Filter_defaults_to_confirmed_and_accepts_repeated_or_comma_separated_names()
    {
        Assert.Equal([S.Confirmed], WaterMeasurementStatuses.ParseFilter(null));
        Assert.Equal([S.Draft, S.Superseded, S.Voided], WaterMeasurementStatuses.ParseFilter(["draft,Corrected", " Voided "]));
        Assert.Equal([S.Draft, S.Confirmed, S.Superseded, S.Voided], WaterMeasurementStatuses.ParseFilter(["All"]));
        Assert.Null(WaterMeasurementStatuses.ParseFilter(["Confirmed", "Deleted"]));
    }

    [Theory]
    [InlineData(S.Draft, false, WaterAlertState.None)]
    [InlineData(S.Confirmed, false, WaterAlertState.None)]
    [InlineData(S.Confirmed, true, WaterAlertState.Active)]
    [InlineData(S.Superseded, true, WaterAlertState.Corrected)]
    [InlineData(S.Voided, true, WaterAlertState.Voided)]
    public void Alert_state_is_derived_from_status_and_exceedance(int statusId, bool exceeded, WaterAlertState expected) =>
        Assert.Equal(expected, WaterMeasurementStatuses.AlertStateOf(new WaterMeasurement { StatusId = statusId, ThresholdExceeded = exceeded }));
}
