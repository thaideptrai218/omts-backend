using OMTS.Application.Features.WaterQuality;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Entities.Cultivation;
using S = OMTS.Domain.Constants.StatusIds.WaterMeasurement;

namespace OMTS.Application.UnitTests.Features.WaterQuality;

public class WaterMeasurementStatusesTests
{
    [Fact]
    public void Filter_defaults_to_active_and_accepts_repeated_or_comma_separated_names()
    {
        Assert.Equal([S.Active], WaterMeasurementStatuses.ParseFilter(null));
        Assert.Equal([S.Active, S.Voided], WaterMeasurementStatuses.ParseFilter(["active", " Voided "]));
        Assert.Equal([S.Active, S.Voided], WaterMeasurementStatuses.ParseFilter(["Active,Voided"]));
        Assert.Equal([S.Active, S.Voided], WaterMeasurementStatuses.ParseFilter(["All"]));
        Assert.Null(WaterMeasurementStatuses.ParseFilter(["Active", "Deleted"]));
        Assert.Null(WaterMeasurementStatuses.ParseFilter(["Confirmed"]));
        Assert.Null(WaterMeasurementStatuses.ParseFilter(["Draft"]));
    }

    [Theory]
    [InlineData(S.Active, false, WaterAlertState.None)]
    [InlineData(S.Active, true, WaterAlertState.Active)]
    [InlineData(S.Voided, false, WaterAlertState.None)]
    [InlineData(S.Voided, true, WaterAlertState.Voided)]
    public void Alert_state_is_derived_from_status_and_exceedance(int statusId, bool exceeded, WaterAlertState expected) =>
        Assert.Equal(expected, WaterMeasurementStatuses.AlertStateOf(new WaterMeasurement { StatusId = statusId, ThresholdExceeded = exceeded }));
}
