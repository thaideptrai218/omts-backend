using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Identity;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeCertificationRequests;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeProfileRequests;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cooperatives;

/// <summary>
/// FARM-01 to SRS 27/10: the new profile fields (short name, year established, social link, introduction in Vietnamese
/// and English), the mandatory fields, and the preview of the provenance page block (BR-FARM-09, BR-ORIGIN-02).
/// </summary>
public class CooperativeProfileV2Tests
{
    [Fact]
    public async Task New_fields_round_trip_and_are_audited_before_and_after()
    {
        using var api = new AuthApiFactory();
        var cooperative = await api.EnsureCooperativeAsync();
        var owner = await api.NewOwnerAsync();

        var first = await (await owner.PutAsJsonAsync("/api/cooperative/profile", Valid(await RowVersionAsync(owner), b =>
        {
            b["legalRepresentative"] = null;
            b["businessLines"] = null;
        }))).ReadAsync(HttpStatusCode.OK);
        var second = await (await owner.PutAsJsonAsync("/api/cooperative/profile", Valid(first.GetProperty("profile").Text("rowVersion"), b =>
        {
            b["legalRepresentative"] = null;
            b["businessLines"] = null;
            b["socialLink"] = "https://zalo.me/htxtrungnam";
        }))).ReadAsync(HttpStatusCode.OK);
        var profile = await owner.GetFromJsonAsync<JsonElement>("/api/cooperative/profile");

        Assert.Equal(
            ("HTX Trung Nam", 2015, "https://zalo.me/htxtrungnam", "Hợp tác xã nuôi hàu Thái Bình Dương tại Vân Đồn.", "A cooperative farming Pacific oysters in Van Don."),
            (profile.Text("shortName"), profile.GetProperty("yearEstablished").GetInt32(), profile.Text("socialLink"), profile.Text("introductionVi"), profile.Text("introductionEn")));
        // Legal representative and business lines are not in the SRS 27/10 profile: optional.
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("legalRepresentative").ValueKind);
        Assert.Empty(profile.GetProperty("businessLines").EnumerateArray());
        Assert.Equal("MSG-GEN-05", second.Text("messageCode"));

        var updates = await api.QueryAsync(db => db.AuditEvents.AsNoTracking()
            .Where(e => e.RecordId == cooperative.Id && e.Action == "Cooperative.Updated").OrderBy(e => e.OccurredAt).ToListAsync());
        Assert.Equal(2, updates.Count);
        var created = JsonDocument.Parse(updates[0].After!).RootElement;
        Assert.Equal(("HTX Trung Nam", 2015, "https://www.facebook.com/htxtrungnam"),
            (created.GetProperty("ShortName").GetString(), created.GetProperty("YearEstablished").GetInt32(), created.GetProperty("SocialLink").GetString()));
        Assert.Equal(["SocialLink"], JsonDocument.Parse(updates[1].Before!).RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("https://www.facebook.com/htxtrungnam", JsonDocument.Parse(updates[1].Before!).RootElement.GetProperty("SocialLink").GetString());
        Assert.Equal("https://zalo.me/htxtrungnam", JsonDocument.Parse(updates[1].After!).RootElement.GetProperty("SocialLink").GetString());
    }

    [Fact]
    public async Task Short_name_year_social_link_and_introductions_are_checked_and_nothing_is_saved()
    {
        using var api = new StillClockApiFactory(DateTimeOffset.UtcNow);
        await api.EnsureCooperativeAsync();
        var owner = await api.NewOwnerAsync();
        var rowVersion = await RowVersionAsync(owner);
        var year = api.Today.Year;

        var invalid = await owner.PutAsJsonAsync("/api/cooperative/profile", Valid(rowVersion, b =>
        {
            b["shortName"] = new string('s', 101);
            b["yearEstablished"] = 1899;
            b["socialLink"] = "http://www.facebook.com/htxtrungnam";
            b["introductionVi"] = new string('v', 4001);
            b["introductionEn"] = new string('e', 4001);
        }));
        var blankShortName = await owner.PutAsJsonAsync("/api/cooperative/profile", Valid(rowVersion, b => b["shortName"] = "  "));
        var nextYear = await owner.PutAsJsonAsync("/api/cooperative/profile", Valid(rowVersion, b => b["yearEstablished"] = year + 1));
        var script = await owner.PutAsJsonAsync("/api/cooperative/profile", Valid(rowVersion, b => b["socialLink"] = "javascript:alert(1)"));
        var tooLongLink = await owner.PutAsJsonAsync("/api/cooperative/profile", Valid(rowVersion, b => b["socialLink"] = $"https://www.facebook.com/{new string('s', 231)}"));

        Assert.Equal(["Short name", "Year established", "Social link", "Introduction (Vietnamese)", "Introduction (English)"], await invalid.InvalidFieldsAsync());
        Assert.Equal("Short name is missing or invalid. Please check and enter it again.", (await blankShortName.Content.ReadFromJsonAsync<JsonElement>()).Text("detail"));
        Assert.Equal(["Year established"], await nextYear.InvalidFieldsAsync());
        Assert.Equal(["Social link"], await script.InvalidFieldsAsync());
        Assert.Equal(["Social link"], await tooLongLink.InvalidFieldsAsync());
        Assert.False(await api.QueryAsync(db => db.AuditEvents.AnyAsync(e => e.RecordType == nameof(Cooperative))));

        // The bounds themselves are accepted.
        foreach (var bound in new[] { 1900, year })
        {
            var saved = await owner.PutAsJsonAsync("/api/cooperative/profile", Valid(await RowVersionAsync(owner), b => b["yearEstablished"] = bound));
            Assert.Equal(bound, (await saved.ReadAsync(HttpStatusCode.OK)).GetProperty("profile").GetProperty("yearEstablished").GetInt32());
        }
    }

    // BR-FARM-09: the public block never holds the tax code or an Expired certification; BR-ORIGIN-02: the English
    // introduction falls back to the Vietnamese one.
    [Fact]
    public async Task Preview_shows_the_public_block_without_the_tax_code_and_expired_certifications()
    {
        using var api = new StillClockApiFactory(DateTimeOffset.UtcNow);
        await api.EnsureCooperativeAsync();
        var owner = await api.NewOwnerAsync();
        await (await owner.PutAsJsonAsync("/api/cooperative/profile", Valid(await RowVersionAsync(owner), b => b["introductionEn"] = null)))
            .ReadAsync(HttpStatusCode.OK);
        await owner.CreateCertificationAsync(new CertificationFields { Name = "A expired", Number = "EXP-1", ExpiryDate = api.Today.AddDays(-1) });
        await owner.CreateCertificationAsync(new CertificationFields { Name = "B expiring", Number = "SOON-1", Issuer = "Quacert", ExpiryDate = api.Today.AddDays(30) });
        await owner.CreateCertificationAsync(new CertificationFields { Name = "C valid", Number = "OK-1", ExpiryDate = api.Today.AddDays(31) });

        var profile = await owner.GetFromJsonAsync<JsonElement>("/api/cooperative/profile");
        var preview = profile.GetProperty("publicPreview");

        Assert.Equal(["A expired", "B expiring", "C valid"], profile.GetProperty("certifications").EnumerateArray().Select(c => c.Text("name")));
        Assert.Equal(
            ("Hợp tác xã Nuôi trồng thủy sản Trung Nam", "HTX Trung Nam", 2015, "0912345678", "https://www.facebook.com/htxtrungnam"),
            (preview.Text("fullName"), preview.Text("shortName"), preview.GetProperty("yearEstablished").GetInt32(), preview.Text("phoneNumber"), preview.Text("socialLink")));
        Assert.Equal(preview.Text("introductionVi"), preview.Text("introductionEn"));
        var certifications = preview.GetProperty("certifications").EnumerateArray().ToList();
        Assert.Equal(["B expiring", "C valid"], certifications.Select(c => c.Text("name")));
        Assert.Equal(["ExpiringSoon", "Valid"], certifications.Select(c => c.Text("state")));
        Assert.Equal(("SOON-1", "Quacert", api.Today.AddDays(30)), (certifications[0].Text("number"), certifications[0].Text("issuer"), DateOnly.Parse(certifications[0].Text("expiryDate"))));
        Assert.Equal(["name", "number", "issuer", "expiryDate", "state"], certifications[0].EnumerateObject().Select(p => p.Name));

        var raw = preview.GetRawText();
        Assert.DoesNotContain("taxCode", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0109876543", raw);
        Assert.DoesNotContain("EXP-1", raw);
        Assert.DoesNotContain("Nguyễn Văn Nam", raw);
        Assert.DoesNotContain("Thương mại thủy sản", raw);
        Assert.DoesNotContain("/api/files/", raw);
    }
}
