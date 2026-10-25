using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// QA of the shared FARM status change helper (OMTS-214): zones and rafts keep the rules they had before the merge
/// (reason trimmed, at most 1000 characters, checked before the row version; one audit row with their own action).
/// </summary>
public class StatusChangeQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Theory]
    [InlineData("FarmingZone")]
    [InlineData("Raft")]
    public async Task Reason_rules_row_version_and_audit_row_are_those_of_before_the_merge(string recordType)
    {
        var owner = await factory.OwnerAsync();
        var zone = await owner.Client.CreateZoneAsync();
        var record = recordType == "Raft" ? await owner.Client.CreateRaftAsync(zone.Id()) : zone;
        var path = recordType == "Raft" ? $"/api/rafts/{record.Id()}" : $"/api/zones/{record.Id()}";

        // Reason first, then the row version: a too long reason with a stale version is MSG01 "Reason".
        Assert.Equal(["Reason"], await (await owner.Client.PostAsJsonAsync($"{path}/discontinue",
            new { reason = new string('r', 1001), rowVersion = "AAAAAAAAB9E=" })).InvalidFieldsAsync());
        Assert.Equal(["RowVersion"], await (await owner.Client.PostAsJsonAsync($"{path}/discontinue", new { rowVersion = "not base64" })).InvalidFieldsAsync());
        Assert.Equal("MSG06", await (await owner.Client.PostAsJsonAsync($"{path}/discontinue", new { rowVersion = "AAAAAAAAB9E=" })).ErrorCodeAsync());

        // 1000 characters once trimmed are accepted; a blank reason is stored as none.
        var discontinued = await (await owner.Client.PostAsJsonAsync($"{path}/discontinue",
            new { reason = $"  {new string('r', 1000)}  ", rowVersion = record.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(["Reason"], await (await owner.Client.PostAsJsonAsync($"{path}/reactivate",
            new { reason = new string('r', 1001), rowVersion = discontinued.Text("rowVersion") })).InvalidFieldsAsync());
        await (await owner.Client.PostAsJsonAsync($"{path}/reactivate", new { reason = "   ", rowVersion = discontinued.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.OK);
        var again = await owner.Client.PostAsJsonAsync($"{path}/reactivate", new { rowVersion = discontinued.Text("rowVersion") });
        Assert.Equal("MSG61", await again.ErrorCodeAsync());

        var audits = await factory.QueryAsync(db => db.AuditEvents.AsNoTracking()
            .Where(e => e.RecordId == record.Id() && e.Action != $"{recordType}.Created").OrderBy(e => e.OccurredAt).ToListAsync());
        Assert.Equal([$"{recordType}.Discontinued", $"{recordType}.Reactivated"], audits.Select(a => a.Action));
        Assert.Equal(new string('r', 1000), audits[0].Reason);
        Assert.Null(audits[1].Reason);
        Assert.Equal(("IN_USE", "OUT_OF_USE"), (Status(audits[0].Before), Status(audits[0].After)));
        Assert.Equal(("OUT_OF_USE", "IN_USE"), (Status(audits[1].Before), Status(audits[1].After)));
        Assert.All(audits, a =>
        {
            Assert.Equal(recordType, a.RecordType);
            Assert.Equal(record.Text("code"), a.RecordCode);
            Assert.Equal(zone.Id(), a.ZoneId);
            Assert.Equal(owner.Account.Id, a.ActorAccountId);
        });
    }

    private static string? Status(string? json) => JsonNode.Parse(json!)!["Status"]!.GetValue<string>();
}
