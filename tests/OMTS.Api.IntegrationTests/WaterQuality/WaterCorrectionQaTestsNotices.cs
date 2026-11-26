using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>QA of WATER-02.2 and BR-WATER-05 against the SRS text: what a void sends.</summary>
public class WaterCorrectionQaTestsNotices(AuthApiFactory api) : WaterNoticeTestBase(api), IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Voiding_the_only_measurement_with_a_value_outside_sends_one_withdrawal()
    {
        var (z, manager) = await ZoneAsync();
        var id = await RecordAsync(z, 9m, 31m, Latest);

        await z.Owner.VoidAsync(id, reason: "  Đo nhầm vùng  ");

        var withdrawn = Assert.Single(await Api.WithdrawalsOfAsync(id), n => n.RecipientAccountId == manager.Id);
        Assert.EndsWith("voided the measurement. Reason: Đo nhầm vùng.", withdrawn.Body);
        Assert.Contains("+07:00", withdrawn.MessageParams);
        Assert.Contains("\"corrected_or_voided\":\"voided\"", withdrawn.MessageParams);
    }

}
