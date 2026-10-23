using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Identity;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeProfileRequests;

namespace OMTS.Api.IntegrationTests.Cooperatives;

/// <summary>
/// FARM-01 logo: real file type by signature, 2 MB limit, audit of the URL change, storage failure (MSG05)
/// and the bank account that the logo endpoints never return or change. Each test gets its own database.
/// </summary>
public class CooperativeLogoRuleTests
{
    private const int TwoMegabytes = 2 * 1024 * 1024;

    [Fact]
    public async Task Jpeg_and_webp_up_to_exactly_2MB_are_accepted_served_publicly_and_audited()
    {
        using var api = new AuthApiFactory();
        var cooperative = await api.EnsureCooperativeAsync();
        var owner = await OwnerAsync(api);

        var jpeg = await UploadLogoAsync(owner, [.. Jpeg, .. new byte[TwoMegabytes - Jpeg.Length]], "logo.jpg");
        var jpegKey = await LogoKeyAsync(api);
        var webp = await UploadLogoAsync(owner, WebP, "logo.webp");
        var logoUrl = (await webp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("logoUrl").GetString()!;
        var served = await api.CreateClient().GetAsync(logoUrl);

        Assert.Equal(HttpStatusCode.OK, jpeg.StatusCode);
        Assert.EndsWith(".jpg", jpegKey);
        Assert.Equal(HttpStatusCode.OK, webp.StatusCode);
        Assert.Equal("image/webp", served.Content.Headers.ContentType?.MediaType);
        Assert.Equal(["nosniff"], served.Headers.GetValues("X-Content-Type-Options"));
        Assert.Equal(WebP, await served.Content.ReadAsByteArrayAsync());
        var changes = await api.QueryAsync(db => db.AuditEvents.AsNoTracking()
            .Where(e => e.RecordId == cooperative.Id && e.Action == "Cooperative.LogoChanged").OrderBy(e => e.OccurredAt).ToListAsync());
        Assert.Equal(2, changes.Count);
        Assert.Equal(jpegKey, JsonDocument.Parse(changes[1].Before!).RootElement.GetProperty("LogoUrl").GetString());
        Assert.Equal(await LogoKeyAsync(api), JsonDocument.Parse(changes[1].After!).RootElement.GetProperty("LogoUrl").GetString());
    }

    [Fact]
    public async Task Pdf_gif_empty_oversized_and_missing_files_are_refused_and_the_logo_stays()
    {
        using var api = new AuthApiFactory();
        await api.EnsureCooperativeAsync();
        var owner = await OwnerAsync(api);
        Assert.Equal(HttpStatusCode.OK, (await UploadLogoAsync(owner, Png, "logo.png")).StatusCode);
        var key = await LogoKeyAsync(api);

        var refused = new[]
        {
            await UploadLogoAsync(owner, Pdf, "logo.png"),
            await UploadLogoAsync(owner, Gif, "logo.png"),
            await UploadLogoAsync(owner, [], "logo.png"),
            await UploadLogoAsync(owner, [.. Png, .. new byte[TwoMegabytes - Png.Length + 1]], "logo.png"),
            await UploadLogoAsync(owner, Png, "logo.png", field: "image"),
        };

        foreach (var response in refused)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("Logo is invalid. Please check and enter it again.", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        }

        Assert.Equal(key, await LogoKeyAsync(api));
        Assert.Equal(1, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "Cooperative.LogoChanged")));
    }

    [Fact]
    public async Task Storage_failure_returns_502_MSG05_and_keeps_the_old_logo()
    {
        using var api = new AuthApiFactory();
        var cooperative = await api.EnsureCooperativeAsync();
        const string oldKey = "cooperative-logos/2026/10/old-logo.png";
        await api.UpdateAsync<Cooperative>(cooperative.Id, c => c.LogoUrl = oldKey);
        var owner = await OwnerAsync(api);
        // A file where the storage folder should be: the Local provider cannot create its folders.
        await File.WriteAllTextAsync(api.StorageRoot, "not a folder");
        try
        {
            var response = await UploadLogoAsync(owner, Png, "logo.png");
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            Assert.Equal("MSG05", problem.GetProperty("code").GetString());
            Assert.Equal("The file operation could not be completed. The source data has not changed; please try again.", problem.GetProperty("detail").GetString());
            Assert.Equal(oldKey, await LogoKeyAsync(api));
            Assert.False(await api.QueryAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "Cooperative.LogoChanged")));
        }
        finally
        {
            File.Delete(api.StorageRoot);
        }
    }

    [Fact]
    public async Task Logo_upload_and_removal_never_return_or_change_the_bank_account()
    {
        using var api = new AuthApiFactory();
        var cooperative = await api.EnsureCooperativeAsync();
        await api.UpdateAsync<Cooperative>(cooperative.Id, c => c.BankAccount = new BankAccountInfo { BankName = "BIDV", AccountNumber = "21510001234567", AccountHolderName = "HTX TRUNG NAM" });
        var owner = await OwnerAsync(api);

        var uploaded = await (await UploadLogoAsync(owner, Png, "logo.png")).Content.ReadFromJsonAsync<JsonElement>();
        var removed = await (await owner.DeleteAsync("/api/cooperative/profile/logo")).Content.ReadFromJsonAsync<JsonElement>();
        var read = await owner.GetFromJsonAsync<JsonElement>("/api/cooperative/profile");

        foreach (var body in new[] { uploaded, removed, read })
        {
            Assert.DoesNotContain(body.EnumerateObject(), p => p.Name.Contains("bank", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("21510001234567", body.GetRawText());
        }

        var stored = await api.QueryAsync(db => db.Cooperatives.AsNoTracking().SingleAsync());
        Assert.Equal(("BIDV", "21510001234567", "HTX TRUNG NAM"), (stored.BankAccount?.BankName, stored.BankAccount?.AccountNumber, stored.BankAccount?.AccountHolderName));
        Assert.Null(stored.LogoUrl);
    }

    [Fact]
    public async Task Public_logo_needs_no_login_and_is_404_without_a_logo()
    {
        using var api = new AuthApiFactory();
        await api.EnsureCooperativeAsync();

        var none = await api.CreateClient().GetAsync("/api/public/cooperative/logo");

        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
    }

    private static Task<string?> LogoKeyAsync(AuthApiFactory api) =>
        api.QueryAsync(db => db.Cooperatives.Select(c => c.LogoUrl).SingleAsync());
}
