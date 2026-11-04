using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>The multipart variant reads numbers with a dot only: a comma is no thousands separator, so 2,5 is not saved as a salinity of 25 (QA of OMTS-221, L1).</summary>
public class WaterDecimalCommaTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    [Theory]
    [InlineData("7,5", "20.5", "pH")]
    [InlineData("7.1", "2,5", "Salinity")]
    [InlineData("7.1", "1,000", "Salinity")]
    public async Task A_decimal_comma_is_refused_and_nothing_is_saved(string ph, string salinity, string field)
    {
        var z = await api.ZoneAsync();
        using var form = WaterTestKit.PhotoForm(WaterTestKit.Photo());
        form.Add(new StringContent(Guid.NewGuid().ToString()), "clientOperationId");
        form.Add(new StringContent(ph), "ph");
        form.Add(new StringContent(salinity), "salinity");
        form.Add(new StringContent(DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O")), "measuredAt");
        form.Add(new StringContent("true"), "confirm");

        var answer = await z.Owner.PostAsync($"{z.Base}/with-photo", form);

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Equal(field, (await answer.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal(0, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
    }
}
