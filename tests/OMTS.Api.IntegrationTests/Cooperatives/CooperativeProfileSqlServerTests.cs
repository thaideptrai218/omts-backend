using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeProfileRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;

namespace OMTS.Api.IntegrationTests.Cooperatives;

/// <summary>FARM-01 behaviour the in-memory provider cannot show: column lengths, real rowversion, the tax-code index.</summary>
public class CooperativeProfileSqlServerTests
{
    // Every field at the longest value FARM-01 allows must fit its column (no 500 on save).
    [SqlServerFact]
    public async Task Profile_at_every_maximum_length_round_trips()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var line = Vietnamese(255);

        var saved = await owner.PutAsJsonAsync("/api/cooperative/profile", Valid(await RowVersionAsync(owner), b =>
        {
            b["fullName"] = Vietnamese(255);
            b["taxCode"] = "0109876543-001";
            b["officeAddress"] = Vietnamese(500);
            b["email"] = $"{new string('e', 60)}@{new string('d', 60)}.{new string('d', 60)}.{new string('d', 60)}.{new string('d', 8)}.vn";
            b["website"] = $"https://htxtrungnam.vn/{new string('w', 232)}";
            b["legalRepresentative"] = Vietnamese(255);
            b["businessLines"] = new[] { line };
        }));

        Assert.True(saved.StatusCode == HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        var stored = await QueryAsync(api, db => db.Cooperatives.AsNoTracking().SingleAsync());
        Assert.Equal((Vietnamese(255), Vietnamese(500), 255), (stored.FullName, stored.OfficeAddress, stored.Website!.Length));
        var reread = await owner.GetFromJsonAsync<JsonElement>("/api/cooperative/profile");
        Assert.Equal([line], reread.GetProperty("businessLines").EnumerateArray().Select(l => l.GetString()));
    }

    // Two tabs (or web and mobile) save the same version at once: one wins, the others get MSG06.
    [SqlServerFact]
    public async Task Parallel_saves_of_one_version_let_exactly_one_win_and_a_late_save_names_the_editor()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, account, cooperative) = await OwnerWithCooperativeAsync(api);
        var rowVersion = await RowVersionAsync(owner);

        var responses = await Task.WhenAll(Enumerable.Range(1, 4).Select(i =>
            owner.PutAsJsonAsync("/api/cooperative/profile", Valid(rowVersion, b => b["fullName"] = $"HTX lần {i}"))));
        var late = await owner.PutAsJsonAsync("/api/cooperative/profile", Valid(rowVersion, b => b["fullName"] = "HTX muộn"));
        var lateProblem = await late.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        foreach (var lost in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            Assert.Equal(HttpStatusCode.Conflict, lost.StatusCode);
            Assert.Equal("MSG06", await lost.ErrorCodeAsync());
        }

        Assert.Equal("MSG06", lateProblem.GetProperty("code").GetString());
        Assert.StartsWith($"The data was just updated by {account.DisplayName} at ", lateProblem.GetProperty("detail").GetString());
        var stored = await QueryAsync(api, db => db.Cooperatives.AsNoTracking().SingleAsync());
        Assert.StartsWith("HTX lần ", stored.FullName);
        Assert.Equal(StatusIds.Cooperative.Completed, stored.StatusId);
        Assert.Equal(1, await QueryAsync(api, db => db.AuditEvents.CountAsync(e => e.RecordId == cooperative.Id && e.Action == "Cooperative.StatusChanged")));
        Assert.Equal(1, await QueryAsync(api, db => db.AuditEvents.CountAsync(e => e.RecordId == cooperative.Id && e.Action == "Cooperative.Updated")));
    }

    // The tax code stays unique even if two records are written without the API check.
    [SqlServerFact]
    public async Task Tax_code_is_unique_in_the_database()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        await ChangeAsync(api, db => db.Cooperatives.Add(new Cooperative { FullName = "HTX A", TaxCode = "0101234567" }));

        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() =>
            ChangeAsync(api, db => db.Cooperatives.Add(new Cooperative { FullName = "HTX B", TaxCode = "0101234567" })));

        Assert.Contains("TaxCode", duplicate.InnerException?.Message);
    }

    /// <summary>Vietnamese text of exactly <paramref name="length"/> characters, with many accented letters.</summary>
    internal static string Vietnamese(int length)
    {
        const string sample = "Nuôi trồng, chế biến và tiêu thụ hàu Thái Bình Dương tại vùng biển Vân Đồn, Quảng Ninh; ";
        var text = string.Concat(Enumerable.Repeat(sample, length / sample.Length + 1))[..length];
        // Saved values are trimmed: never end on a space.
        return char.IsWhiteSpace(text[^1]) ? text[..^1] + "x" : text;
    }
}
