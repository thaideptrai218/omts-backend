using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// ENV-01/02 "missing or invalid value → 400 MSG01" for values the JSON binder cannot read (QA red team): a
/// device sending a malformed id or a number as text must get the same MSG01 problem as any other invalid value.
/// </summary>
public class WaterMalformedInputTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    [Theory]
    [InlineData("{\"clientOperationId\":\"not-a-guid\",\"ph\":7.1,\"salinity\":20,\"confirm\":true}")]
    [InlineData("{\"clientOperationId\":\"7d5f2a8e-58a4-4e0b-9a51-2f0f6c1d9b11\",\"ph\":\"seven\",\"salinity\":20,\"confirm\":true}")]
    [InlineData("{\"clientOperationId\":\"7d5f2a8e-58a4-4e0b-9a51-2f0f6c1d9b12\",\"ph\":7.1,\"salinity\":20,\"measuredAt\":\"yesterday\",\"confirm\":true}")]
    public async Task Unreadable_json_values_are_MSG01_and_nothing_is_saved(string json)
    {
        var z = await api.ZoneAsync();

        var response = await z.Owner.PostAsync(z.Base, new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG01", await response.ErrorCodeAsync());
        Assert.Equal(0, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
    }
}
