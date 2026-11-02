using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeProfileRequests;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeProfileSqlServerTests;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;

namespace OMTS.Api.IntegrationTests.Cooperatives;

/// <summary>
/// FARM-01 allows any number of business lines of up to 255 characters each. They are stored as one JSON
/// array in Cooperatives.BusinessLines (nvarchar(2000)), and the default JSON encoder writes every
/// Vietnamese letter as a 6-character \uXXXX escape.
/// </summary>
public class CooperativeBusinessLinesColumnTests
{
    [SqlServerFact]
    public async Task Four_long_Vietnamese_business_lines_are_saved()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var lines = Enumerable.Range(1, 4).Select(i => $"{i}. {Vietnamese(252)}").ToArray();

        var saved = await owner.PutAsJsonAsync("/api/cooperative/profile", Valid(await RowVersionAsync(owner), b => b["businessLines"] = lines));

        Assert.True(saved.StatusCode == HttpStatusCode.OK, $"HTTP {(int)saved.StatusCode}: {await saved.Content.ReadAsStringAsync()}");
        var reread = await owner.GetFromJsonAsync<JsonElement>("/api/cooperative/profile");
        Assert.Equal(lines, reread.GetProperty("businessLines").EnumerateArray().Select(l => l.GetString()));
    }
}
