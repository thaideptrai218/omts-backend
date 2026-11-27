using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>The employee list for the zones chosen in the header: <c>zoneIds</c> is repeated and keeps the employees working in one of those zones, those of a zone the caller may not see being left out.</summary>
public class EmployeeZoneIdsTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static HashSet<string> NamesOf(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("fullName").GetString()!).ToHashSet();

    [Fact]
    public async Task Repeated_zone_ids_keep_the_employees_of_those_zones_and_the_single_zone_still_works()
    {
        var (owner, zoneA, _) = await SetupAsync(factory);
        var zoneB = await factory.AddZoneAsync(NewCode());
        var zoneC = await factory.AddZoneAsync(NewCode());
        var tag = NewCode();
        var (nameA, nameB, nameC) = ($"Anh {tag}", $"Binh {tag}", $"Chi {tag}");
        await CreateAsync(owner, SeasonalProfile(nameA, NewPhone(), zoneA.Id));
        await CreateAsync(owner, SeasonalProfile(nameB, NewPhone(), zoneB.Id));
        await CreateAsync(owner, SeasonalProfile(nameC, NewPhone(), zoneC.Id));

        var two = await owner.GetFromJsonAsync<JsonElement>($"/api/employees?search={tag}&zoneIds={zoneA.Id}&zoneIds={zoneB.Id}");
        var joined = await owner.GetFromJsonAsync<JsonElement>($"/api/employees?search={tag}&zoneId={zoneC.Id}&zoneIds={zoneA.Id}");
        var single = await owner.GetFromJsonAsync<JsonElement>($"/api/employees?search={tag}&zoneId={zoneB.Id}");

        Assert.Equal(new HashSet<string> { nameA, nameB }, NamesOf(two));
        Assert.Equal(new HashSet<string> { nameA, nameC }, NamesOf(joined));
        Assert.Equal(new HashSet<string> { nameB }, NamesOf(single));
    }

    [Fact]
    public async Task A_zone_manager_gets_nobody_for_zone_ids_none_of_which_they_may_see()
    {
        var (owner, zoneA, _) = await SetupAsync(factory);
        var zoneB = await factory.AddZoneAsync(NewCode());
        var tag = NewCode();
        await CreateAsync(owner, SeasonalProfile($"Anh {tag}", NewPhone(), zoneA.Id));
        await CreateAsync(owner, SeasonalProfile($"Binh {tag}", NewPhone(), zoneB.Id));
        var manager = await ManagerAsync(factory, zoneA);

        var foreign = await manager.GetFromJsonAsync<JsonElement>($"/api/employees?search={tag}&zoneIds={zoneB.Id}");

        Assert.Equal(0, foreign.GetProperty("total").GetInt32());
    }
}
