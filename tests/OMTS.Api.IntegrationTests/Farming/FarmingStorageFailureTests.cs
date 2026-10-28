using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Farming;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>FARM-03/04: a failing file storage answers 502 MSG05 and leaves the data and the storage unchanged.</summary>
public class FarmingStorageFailureTests
{
    [Fact]
    public async Task Storage_failure_answers_MSG05_and_saves_nothing()
    {
        using var factory = new AuthApiFactory();
        // A file where a category folder should be makes the real Local provider fail every upload of that category.
        Directory.CreateDirectory(factory.StorageRoot);
        await File.WriteAllTextAsync(Path.Combine(factory.StorageRoot, "zone-licenses"), "blocked");
        await File.WriteAllTextAsync(Path.Combine(factory.StorageRoot, "raft-photos"), "blocked");

        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var raft = await owner.Client.CreateRaftAsync(zoneId);
        // An existing license, inserted directly because no upload can succeed on this host.
        var existing = new ZoneComplianceDocument
        {
            DocumentType = "AQUACULTURE_LICENSE", DocumentNumber = $"GP-{NewCode()}", EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddYears(1),
            EvidenceFileUrl = $"zone-licenses/2026/01/{Guid.NewGuid():N}.pdf",
        };
        existing.ZoneScopes.Add(new DocumentZoneScope { DocumentId = existing.Id, ZoneId = zoneId, ValidFrom = existing.EffectiveDate, ValidTo = existing.ExpiryDate });
        await factory.UseDbAsync(db => db.ZoneComplianceDocuments.Add(existing));
        var number = $"GP-{NewCode()}";

        var create = await owner.Client.PostAsync("/api/zone-licenses", new LicenseFields { DocumentNumber = number, ZoneIds = [zoneId], EffectiveDate = Today }.ToForm());
        await AssertFileFailureAsync(create);
        var edit = await owner.Client.PutAsync($"/api/zone-licenses/{existing.Id}", new LicenseFields
        {
            DocumentNumber = existing.DocumentNumber, Holder = "Changed holder", ZoneIds = [zoneId], EffectiveDate = existing.EffectiveDate, ExpiryDate = existing.ExpiryDate,
            RowVersion = await factory.IssueRowVersionAsync<ZoneComplianceDocument>(existing.Id),
        }.ToForm());
        await AssertFileFailureAsync(edit);
        var replace = await owner.Client.PostAsync($"/api/zone-licenses/{existing.Id}/replace", new LicenseFields { DocumentNumber = number, EffectiveDate = Today }.ToForm());
        await AssertFileFailureAsync(replace);
        var photo = await owner.Client.PostAsync($"/api/rafts/{raft.Id()}/photo", new MultipartFormDataContent { { FileContent(Png), "file", "raft.png" } });
        await AssertFileFailureAsync(photo);

        Assert.Equal(0, await factory.QueryAsync(db => db.ZoneComplianceDocuments.CountAsync(d => d.DocumentNumber == number || d.ReplacesDocumentId == existing.Id)));
        var kept = await factory.QueryAsync(db => db.ZoneComplianceDocuments.SingleAsync(d => d.Id == existing.Id));
        Assert.Null(kept.Holder);
        Assert.Equal(existing.EvidenceFileUrl, kept.EvidenceFileUrl);
        Assert.Null((await factory.QueryAsync(db => db.Rafts.SingleAsync(r => r.Id == raft.Id()))).PhotoUrl);
        Assert.Equal(0, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordType == "ZoneLicense" || (e.RecordId == raft.Id() && e.Action == "Raft.Updated"))));
        // Only the two blocking files: no stored file was left behind.
        Assert.Equal(2, Directory.GetFileSystemEntries(factory.StorageRoot, "*", SearchOption.AllDirectories).Length);
    }

    private static async Task AssertFileFailureAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("MSG-GEN-03", await response.ErrorCodeAsync());
    }
}
