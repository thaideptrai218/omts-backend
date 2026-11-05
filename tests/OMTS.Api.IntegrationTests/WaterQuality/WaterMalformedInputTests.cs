using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// WATER-01/02 "missing or invalid value → 400 MSG-GEN-01" for values the JSON binder cannot read (QA red team): a
/// client sending a malformed id or a number as text must get the same problem as any other invalid value.
/// </summary>
public class WaterMalformedInputTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    [Theory]
    [InlineData("{\"clientOperationId\":\"not-a-guid\",\"ph\":7.1,\"salinity\":20,\"temperature\":25}")]
    [InlineData("{\"raftId\":\"not-a-guid\",\"ph\":7.1,\"salinity\":20,\"temperature\":25}")]
    [InlineData("{\"ph\":\"seven\",\"salinity\":20,\"temperature\":25}")]
    [InlineData("{\"ph\":7.1,\"salinity\":20,\"temperature\":\"warm\"}")]
    [InlineData("{\"ph\":7.1,\"salinity\":20,\"temperature\":25,\"measuredAt\":\"yesterday\"}")]
    public async Task Unreadable_json_values_are_MSG01_and_nothing_is_saved(string json)
    {
        var z = await api.ZoneAsync();

        var response = await z.Owner.PostAsync(z.Base, new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG-GEN-01", await response.ErrorCodeAsync());
        Assert.Equal(0, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
    }
}
