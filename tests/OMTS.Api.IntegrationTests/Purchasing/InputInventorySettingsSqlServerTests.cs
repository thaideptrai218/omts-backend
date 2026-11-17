using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// The inventory settings of the cooperative (cost method and negative stock) against a stale row version. The in-memory provider generates no row version, so the refusal is checked here, on
/// SQL Server (the rest of the settings rules are in <see cref="InputItemCatalogueTests"/>).
/// </summary>
public sealed class InputInventorySettingsSqlServerTests
{
    private const string Settings = "/api/input-inventory-settings";

    [SqlServerFact]
    public async Task A_stale_row_version_of_the_settings_is_refused_with_a_conflict()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var settings = await owner.GetFromJsonAsync<JsonElement>(Settings);
        var version = settings.GetProperty("rowVersion").GetString();

        await (await owner.PutAsJsonAsync(Settings, new { costMethod = "Average", allowNegativeQuantity = false, rowVersion = version })).ReadAsync(HttpStatusCode.OK);

        var stale = await owner.PutAsJsonAsync(Settings, new { costMethod = "Fixed", allowNegativeQuantity = true, rowVersion = version });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }
}
