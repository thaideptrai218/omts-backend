using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Cooperatives;

/// <summary>FARM-01 – cooperative profile, Owner only. Each test gets its own database (one cooperative).</summary>
public class CooperativeProfileTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D];

    [Fact]
    public async Task First_complete_save_makes_the_profile_Completed_and_keeps_the_bank_account()
    {
        using var api = new AuthApiFactory();
        var cooperative = await api.EnsureCooperativeAsync();
        await api.UpdateAsync<Cooperative>(cooperative.Id, c => c.BankAccount = new BankAccountInfo { BankName = "VCB", AccountNumber = "0011223344", AccountHolderName = "HTX" });
        var owner = await OwnerAsync(api);

        var before = await owner.GetFromJsonAsync<JsonElement>("/api/cooperative/profile");
        var saved = await owner.PutAsJsonAsync("/api/cooperative/profile", Valid(before.GetProperty("rowVersion").GetString()));
        var body = await saved.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Preliminary", before.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("Cooperative profile saved.", body.GetProperty("message").GetString());
        Assert.Equal("Completed", body.GetProperty("profile").GetProperty("status").GetString());
        Assert.Equal(["Nuôi hàu", "Thương mại thủy sản"], body.GetProperty("profile").GetProperty("businessLines").EnumerateArray().Select(l => l.GetString()));
        Assert.DoesNotContain("bankAccount", body.GetProperty("profile").EnumerateObject().Select(p => p.Name));
        var preview = body.GetProperty("profile").GetProperty("publicPreview");
        Assert.Equal(["name", "officeAddress", "logoUrl"], preview.EnumerateObject().Select(p => p.Name));

        var stored = await api.QueryAsync(db => db.Cooperatives.AsNoTracking().SingleAsync());
        Assert.Equal("0011223344", stored.BankAccount?.AccountNumber);
        var actions = await api.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == cooperative.Id).Select(e => e.Action).ToListAsync());
        Assert.Contains("Cooperative.Updated", actions);
        Assert.Contains("Cooperative.StatusChanged", actions);
    }

    [Fact]
    public async Task Every_invalid_field_is_named_and_nothing_is_saved()
    {
        using var api = new AuthApiFactory();
        await api.EnsureCooperativeAsync();
        var owner = await OwnerAsync(api);
        var rowVersion = (await owner.GetFromJsonAsync<JsonElement>("/api/cooperative/profile")).GetProperty("rowVersion").GetString();

        var response = await owner.PutAsJsonAsync("/api/cooperative/profile", new
        {
            fullName = "HTX", taxCode = "12345", officeAddress = "Vân Đồn", phoneNumber = "0912345678",
            website = "ftp://htx.vn", legalRepresentative = "Ông A", businessLines = new[] { " " }, rowVersion,
        });
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Tax code is invalid. Please check and enter it again.", problem.GetProperty("detail").GetString());
        Assert.Equal(["Tax code", "Website", "Business lines"], problem.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(StatusIds.Cooperative.Draft, await api.QueryAsync(db => db.Cooperatives.Select(c => c.StatusId).SingleAsync()));
    }

    [Fact]
    public async Task Tax_code_of_another_record_and_a_stale_version_are_refused()
    {
        using var api = new AuthApiFactory();
        await api.EnsureCooperativeAsync();
        await api.UseDbAsync(db => db.Cooperatives.Add(new Cooperative { FullName = "Other", TaxCode = "0101234567", CreatedAt = DateTimeOffset.UtcNow.AddDays(1) }));
        var owner = await OwnerAsync(api);
        var rowVersion = (await owner.GetFromJsonAsync<JsonElement>("/api/cooperative/profile")).GetProperty("rowVersion").GetString();

        var duplicate = await owner.PutAsJsonAsync("/api/cooperative/profile", Valid(rowVersion, taxCode: "0101234567"));
        var stale = await owner.PutAsJsonAsync("/api/cooperative/profile", Valid("AAAAAAAAB9E="));

        Assert.Equal("MSG03", await duplicate.ErrorCodeAsync());
        Assert.Equal("MSG06", await stale.ErrorCodeAsync());
    }

    [Fact]
    public async Task Only_the_owner_reads_or_edits_the_profile()
    {
        using var api = new AuthApiFactory();
        await api.EnsureCooperativeAsync();
        var staff = await api.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var role = await api.AddRoleAsync($"R {NewCode()}", (Application.Features.Authorization.PermissionFunctions.WaterMeasurements, true, true, true));
        await api.GrantAsync(staff, role, null);
        var client = await api.LoginAsync(staff.Username);

        Assert.Equal("MSG02", await (await client.GetAsync("/api/cooperative/profile")).ErrorCodeAsync());
        Assert.Equal("MSG02", await (await client.PutAsJsonAsync("/api/cooperative/profile", Valid(""))).ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/cooperative/profile")).StatusCode);
    }

    [Fact]
    public async Task Logo_is_checked_by_content_and_size_served_publicly_and_old_files_are_kept()
    {
        using var api = new AuthApiFactory();
        await api.EnsureCooperativeAsync();
        var owner = await OwnerAsync(api);

        var exe = await UploadAsync(owner, [0x4D, 0x5A, 0x90, 0, 3, 0, 0, 0, 4, 0, 0, 0], "logo.png");
        var big = await UploadAsync(owner, [.. Png, .. new byte[2 * 1024 * 1024]], "logo.png");
        var first = await UploadAsync(owner, Png, "logo.png");
        var firstKey = await api.QueryAsync(db => db.Cooperatives.Select(c => c.LogoUrl).SingleAsync());
        var second = await UploadAsync(owner, Png, "logo2.png");
        var logoUrl = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("logoUrl").GetString()!;
        var publicLogo = await api.CreateClient().GetAsync(logoUrl);
        var removed = await owner.DeleteAsync("/api/cooperative/profile/logo");

        Assert.Equal("Logo is invalid. Please check and enter it again.", (await exe.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        Assert.Equal("MSG01", await big.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, publicLogo.StatusCode);
        Assert.Equal("image/png", publicLogo.Content.Headers.ContentType?.MediaType);
        Assert.Equal(JsonValueKind.Null, (await removed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("logoUrl").ValueKind);
        Assert.True(File.Exists(Path.Combine(api.StorageRoot, firstKey!)));
        Assert.Equal(HttpStatusCode.NotFound, (await api.CreateClient().GetAsync(logoUrl)).StatusCode);
    }

    private static async Task<HttpClient> OwnerAsync(AuthApiFactory api) =>
        await api.LoginAsync((await api.AddAccountAsync(NewCode(), Password)).Username);

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] content, string fileName)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", fileName);
        return client.PostAsync("/api/cooperative/profile/logo", form);
    }

    private static object Valid(string? rowVersion, string taxCode = "0109876543") => new
    {
        fullName = "Hợp tác xã Nuôi trồng thủy sản Trung Nam",
        taxCode,
        officeAddress = "Thôn 3, xã Đông Xá, Vân Đồn, Quảng Ninh",
        phoneNumber = "+84912345678",
        email = "lienhe@htxtrungnam.vn",
        website = "https://htxtrungnam.vn",
        legalRepresentative = "Nguyễn Văn Nam",
        businessLines = new[] { "Nuôi hàu", " Thương mại thủy sản " },
        rowVersion,
    };
}
