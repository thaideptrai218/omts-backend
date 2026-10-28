using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeProfileRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Cooperatives;

/// <summary>
/// FARM-01 rules and error bullets not covered by <see cref="CooperativeProfileTests"/>: every non-Owner
/// caller, every field rule, the status that never goes back, audit of changed fields only and MSG06.
/// Each test gets its own database (one cooperative).
/// </summary>
public class CooperativeProfileRuleTests
{
    [Fact]
    public async Task Traders_staff_and_anonymous_callers_cannot_use_any_profile_or_logo_endpoint()
    {
        using var api = new AuthApiFactory();
        await api.EnsureCooperativeAsync();
        var trader = await api.LoginAsync((await api.AddAccountAsync(NewCode(), Password, SubjectType.Trader)).Username);
        var manager = await api.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        // A custom role with every labor and provenance right still never opens an Owner-only function.
        var role = await api.AddRoleAsync($"ZM {NewCode()}",
            (PermissionFunctions.Provenance, true, true, true), (PermissionFunctions.LaborWorkers, true, true, true));
        await api.GrantAsync(manager, role, null);
        var staff = await api.LoginAsync(manager.Username);

        foreach (var client in new[] { trader, staff })
        {
            Assert.Equal("MSG-GEN-08", await (await client.GetAsync("/api/cooperative/profile")).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.PutAsJsonAsync("/api/cooperative/profile", Valid("AAAAAAAAB9E="))).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await UploadLogoAsync(client, Png, "logo.png")).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.DeleteAsync("/api/cooperative/profile/logo")).ErrorCodeAsync());
        }

        var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync("/api/cooperative/profile", Valid(""))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await UploadLogoAsync(anonymous, Png, "logo.png")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync("/api/cooperative/profile/logo")).StatusCode);
        Assert.Null(await api.QueryAsync(db => db.Cooperatives.Select(c => c.LogoUrl).SingleAsync()));
        Assert.Equal(StatusIds.Cooperative.Draft, await api.QueryAsync(db => db.Cooperatives.Select(c => c.StatusId).SingleAsync()));
    }

    [Fact]
    public async Task Every_missing_required_field_is_named_in_form_order()
    {
        using var api = new AuthApiFactory();
        await api.EnsureCooperativeAsync();
        var owner = await OwnerAsync(api);
        var rowVersion = await RowVersionAsync(owner);

        var response = await owner.PutAsJsonAsync("/api/cooperative/profile", new { fullName = "  ", rowVersion });
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Full name is missing or invalid. Please check and enter it again.", problem.GetProperty("detail").GetString());
        Assert.Equal(
            ["Full name", "Tax code", "Office address", "Phone number", "Legal representative", "Business lines"],
            Fields(problem));
        Assert.False(await api.QueryAsync(db => db.AuditEvents.AnyAsync(e => e.RecordType == nameof(Cooperative))));
    }

    [Fact]
    public async Task Overlong_and_malformed_values_are_named_and_nothing_is_saved()
    {
        using var api = new AuthApiFactory();
        await api.EnsureCooperativeAsync();
        var owner = await OwnerAsync(api);
        var rowVersion = await RowVersionAsync(owner);

        var overlong = await owner.PutAsJsonAsync("/api/cooperative/profile", Valid(rowVersion, b =>
        {
            b["fullName"] = new string('H', 256);
            b["officeAddress"] = new string('A', 501);
            b["email"] = $"{new string('e', 250)}@htx.vn";
            b["website"] = $"https://{new string('w', 245)}.vn";
            b["legalRepresentative"] = new string('L', 256);
            b["businessLines"] = new[] { "Nuôi hàu", new string('B', 256) };
        }));
        var malformed = await owner.PutAsJsonAsync("/api/cooperative/profile", Valid(rowVersion, b =>
        {
            b["taxCode"] = "0101234567-12";
            b["phoneNumber"] = "0812345";
            b["email"] = "lienhe-at-htx";
            b["website"] = "javascript:alert(1)";
        }));
        var noLines = await owner.PutAsJsonAsync("/api/cooperative/profile", Valid(rowVersion, b => b["businessLines"] = Array.Empty<string>()));

        Assert.Equal(
            ["Full name", "Office address", "Email", "Website", "Legal representative", "Business lines"],
            Fields(await overlong.Content.ReadFromJsonAsync<JsonElement>()));
        Assert.Equal(["Tax code", "Phone number", "Email", "Website"], Fields(await malformed.Content.ReadFromJsonAsync<JsonElement>()));
        Assert.Equal(["Business lines"], Fields(await noLines.Content.ReadFromJsonAsync<JsonElement>()));
        var stored = await api.QueryAsync(db => db.Cooperatives.AsNoTracking().SingleAsync());
        Assert.Equal(("HTX Trung Nam", null, StatusIds.Cooperative.Draft), (stored.FullName, stored.TaxCode, stored.StatusId));
    }

    [Fact]
    public async Task Branch_tax_code_is_accepted_and_plus84_phone_is_stored_as_0()
    {
        using var api = new AuthApiFactory();
        await api.EnsureCooperativeAsync();
        var owner = await OwnerAsync(api);

        var saved = await owner.PutAsJsonAsync("/api/cooperative/profile", Valid(await RowVersionAsync(owner), b =>
        {
            b["taxCode"] = " 0109876543-001 ";
            b["phoneNumber"] = "+84 912 345 678";
            b["email"] = null;
            b["website"] = null;
        }));
        var profile = (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("profile");

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("0109876543-001", profile.GetProperty("taxCode").GetString());
        Assert.Equal("0912345678", profile.GetProperty("phoneNumber").GetString());
        Assert.Equal("0912345678", await api.QueryAsync(db => db.Cooperatives.Select(c => c.PhoneNumber).SingleAsync()));
    }

    [Fact]
    public async Task Completed_profile_never_returns_to_Preliminary_and_audits_only_changed_fields()
    {
        using var api = new AuthApiFactory();
        var cooperative = await api.EnsureCooperativeAsync();
        var owner = await OwnerAsync(api);
        var first = await (await owner.PutAsJsonAsync("/api/cooperative/profile", Valid(await RowVersionAsync(owner))))
            .Content.ReadFromJsonAsync<JsonElement>();

        var second = await owner.PutAsJsonAsync("/api/cooperative/profile", Valid(
            first.GetProperty("profile").GetProperty("rowVersion").GetString(), b => b["email"] = "banhang@htxtrungnam.vn"));
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("Completed", body.GetProperty("profile").GetProperty("status").GetString());
        var events = await api.QueryAsync(db => db.AuditEvents.AsNoTracking()
            .Where(e => e.RecordId == cooperative.Id).OrderBy(e => e.OccurredAt).ToListAsync());
        Assert.Single(events, e => e.Action == "Cooperative.StatusChanged");
        var lastUpdate = events.Last(e => e.Action == "Cooperative.Updated");
        Assert.Equal(["Email"], JsonDocument.Parse(lastUpdate.Before!).RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("lienhe@htxtrungnam.vn", JsonDocument.Parse(lastUpdate.Before!).RootElement.GetProperty("Email").GetString());
        Assert.Equal("banhang@htxtrungnam.vn", JsonDocument.Parse(lastUpdate.After!).RootElement.GetProperty("Email").GetString());
        var status = events.Single(e => e.Action == "Cooperative.StatusChanged");
        Assert.Contains("DRAFT", status.Before, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StatusIds.Cooperative.Completed, await api.QueryAsync(db => db.Cooperatives.Select(c => c.StatusId).SingleAsync()));
    }

    [Fact]
    public async Task Missing_row_version_is_invalid_and_a_stale_one_names_the_last_editor()
    {
        using var api = new AuthApiFactory();
        await api.EnsureCooperativeAsync();
        var ownerAccount = await api.AddAccountAsync(NewCode(), Password);
        var owner = await api.LoginAsync(ownerAccount.Username);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/cooperative/profile", Valid(await RowVersionAsync(owner)))).StatusCode);

        var missing = await owner.PutAsJsonAsync("/api/cooperative/profile", Valid(null, b => b["fullName"] = "HTX Mới"));
        var stale = await owner.PutAsJsonAsync("/api/cooperative/profile", Valid("AAAAAAAAB9E=", b => b["fullName"] = "HTX Mới"));
        var problem = await stale.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("RowVersion", (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("MSG06", problem.GetProperty("code").GetString());
        Assert.StartsWith($"The data was just updated by {ownerAccount.DisplayName} at ", problem.GetProperty("detail").GetString());
        Assert.Equal("Hợp tác xã Nuôi trồng thủy sản Trung Nam", await api.QueryAsync(db => db.Cooperatives.Select(c => c.FullName).SingleAsync()));
    }

    [Fact]
    public async Task Duplicate_tax_code_points_to_the_existing_record_and_saves_nothing()
    {
        using var api = new AuthApiFactory();
        await api.EnsureCooperativeAsync();
        var other = new Cooperative { FullName = "HTX Khác", TaxCode = "0101234567", CreatedAt = DateTimeOffset.UtcNow.AddDays(1) };
        await api.UseDbAsync(db => db.Cooperatives.Add(other));
        var owner = await OwnerAsync(api);

        var duplicate = await owner.PutAsJsonAsync("/api/cooperative/profile", Valid(await RowVersionAsync(owner), b => b["taxCode"] = "0101234567"));
        var problem = await duplicate.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("Tax code already exists. Your entries are kept: change them, or open the existing record.",
            problem.GetProperty("detail").GetString());
        Assert.Equal(other.Id, problem.GetProperty("existingId").GetGuid());
        Assert.Equal(1, await api.QueryAsync(db => db.Cooperatives.CountAsync(c => c.StatusId == StatusIds.Cooperative.Draft && c.TaxCode == null)));
        Assert.False(await api.QueryAsync(db => db.AuditEvents.AnyAsync(e => e.RecordType == nameof(Cooperative))));
    }

    private static List<string?> Fields(JsonElement problem) =>
        problem.GetProperty("fields").EnumerateArray().Select(f => f.GetString()).ToList();
}
