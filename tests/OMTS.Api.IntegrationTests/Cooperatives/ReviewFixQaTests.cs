using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;

namespace OMTS.Api.IntegrationTests.Cooperatives;

/// <summary>
/// QA-1 review of OMTS-199 fix commit 7c927b9: per-file logo URLs (path and category rules, WebP, removed logos,
/// Azurite), LAB-01 Owner-rights rule through the ACS refusal, and the stale-screen checks of end and reinstate.
/// </summary>
public class ReviewFixQaTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x51];
    private static readonly byte[] PngOther = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x52];
    private static readonly byte[] Webp = [.. "RIFF"u8.ToArray(), 0x10, 0, 0, 0, .. "WEBPVP8L"u8.ToArray(), 1, 2, 3, 4];

    // A logo removed from the profile: its own URL keeps serving the file, only the current-logo URL goes away.
    [Fact]
    public async Task Removed_logo_keeps_its_own_url_while_the_current_logo_is_gone()
    {
        using var api = new AuthApiFactory();
        await api.EnsureCooperativeAsync();
        var owner = await api.LoginAsync((await api.AddAccountAsync(NewCode(), Password)).Username);
        var url = await LogoUrlAsync(await UploadAsync(owner, Png, "image/png", "logo.png"));
        var anonymous = api.CreateClient();

        var removed = await owner.DeleteAsync("/api/cooperative/profile/logo");
        var afterRemoval = await removed.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(JsonValueKind.Null, afterRemoval.GetProperty("logoUrl").ValueKind);
        Assert.Equal(Png, await anonymous.GetByteArrayAsync(url));
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/public/cooperative/logo")).StatusCode);
    }

    // A WebP logo: the URL keeps the .webp extension and the file is served as image/webp.
    [Fact]
    public async Task Webp_logo_gets_a_webp_url_served_as_image_webp()
    {
        using var api = new AuthApiFactory();
        await api.EnsureCooperativeAsync();
        var owner = await api.LoginAsync((await api.AddAccountAsync(NewCode(), Password)).Username);
        var url = await LogoUrlAsync(await UploadAsync(owner, Webp, "image/webp", "logo.webp"));

        var served = await api.CreateClient().GetAsync(url);

        Assert.EndsWith(".webp", url);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/webp", served.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Webp, await served.Content.ReadAsByteArrayAsync());
    }

    // A real non-logo file (same storage shape, other category) must stay unreachable through the logo route,
    // whatever the path tricks: encoded slashes, backslashes, double encoding, case, NUL.
    [Fact]
    public async Task Non_logo_file_is_unreachable_through_logo_urls_whatever_the_encoding()
    {
        using var api = new AuthApiFactory();
        var storage = api.Services.GetRequiredService<IFileStorage>();
        var evidence = await storage.UploadAsync(Validated(Png), "evidence");
        Assert.True(evidence.Succeeded);
        var parts = evidence.Key!.Split('/');
        var (year, month, file) = (parts[1], parts[2], parts[3]);
        const string route = "/api/public/cooperative/logos";

        var urls = new[]
        {
            $"{route}/{year}/{month}/{file}",
            $"{route}/..%2Fevidence%2F{year}%2F{month}%2F{file}",
            $"{route}/{year}/{month}/..%2F..%2Fevidence%2F{year}%2F{month}%2F{file}",
            $"{route}/{year}/{month}/%2e%2e%2f%2e%2e%2fevidence%2f{year}%2f{month}%2f{file}",
            $"{route}/{year}/{month}/..%5C..%5Cevidence%5C{year}%5C{month}%5C{file}",
            $"{route}/{year}/{month}/%252e%252e%252fevidence%252f{year}%252f{month}%252f{file}",
            $"{route}/{year}/{month}/{file.ToUpperInvariant()}",
            $"{route}/{year}/{month}/{file}%2F",
        };
        var anonymous = api.CreateClient();
        var statuses = new List<string>();
        foreach (var url in urls)
        {
            statuses.Add($"{(int)(await anonymous.GetAsync(url)).StatusCode} {url}");
        }

        await using (var stored = await storage.OpenReadAsync(evidence.Key!))
        {
            Assert.NotNull(stored);
        }

        Assert.All(statuses, s => Assert.StartsWith("404 ", s));
    }

    // The logo URL is the storage key below the category, nothing more: /logos/{yyyy}/{MM}/{32 hex}.{ext}.
    [Fact]
    public async Task Logo_url_path_shape_is_the_storage_key_and_nothing_else()
    {
        using var api = new AuthApiFactory();
        await api.EnsureCooperativeAsync();
        var owner = await api.LoginAsync((await api.AddAccountAsync(NewCode(), Password)).Username);
        var url = await LogoUrlAsync(await UploadAsync(owner, Png, "image/png", "logo.png"));

        var segments = url.Split('/');

        Assert.Equal(["", "api", "public", "cooperative", "logos", segments[5], segments[6], segments[7]], segments);
        Assert.Matches(@"^[0-9]{4}$", segments[5]);
        Assert.Matches(@"^[0-9]{2}$", segments[6]);
        Assert.Matches(@"^[a-f0-9]{32}\.png$", segments[7]);
    }

    // LAB-01 refuses any non-Staff subject type (ACS rule): 403 MSG02, one audit row, nothing created.
    [Theory]
    [InlineData("CooperativeOwner")]
    [InlineData("Trader")]
    public async Task Non_staff_subject_type_on_a_new_employee_is_403_MSG02_audited_and_creates_nothing(string subjectType)
    {
        using var api = new AuthApiFactory();
        var (owner, zone, _) = await SetupAsync(api);
        var username = $"qa{NewCode()}";

        var response = await owner.PostAsJsonAsync("/api/employees", Profile($"NV {NewCode()}", NewPhone(), zone.Id, b => b["account"] = new
        {
            username,
            subjectType,
            grants = Array.Empty<object>(),
        }));
        var refusals = await api.QueryAsync(db => db.AuditEvents.Where(e => e.Action == "Account.OwnerRightsRefused").Select(e => e.RecordCode).ToListAsync());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("MSG-GEN-08", await response.ErrorCodeAsync());
        Assert.Equal([username], refusals);
        Assert.Equal(0, await api.QueryAsync(db => db.Employees.CountAsync()));
        Assert.False(await api.QueryAsync(db => db.Accounts.AnyAsync(a => a.Username == username)));
    }

    // Control for the refusal: a Staff account is still created with its employee and writes no refusal audit row.
    [Fact]
    public async Task Staff_account_on_a_new_employee_is_created_and_writes_no_refusal()
    {
        using var api = new AuthApiFactory();
        var (owner, zone, role) = await SetupAsync(api);
        var username = $"qa{NewCode()}";

        var response = await owner.PostAsJsonAsync("/api/employees", Profile($"NV {NewCode()}", NewPhone(), zone.Id, b => b["account"] = new
        {
            username,
            subjectType = "Staff",
            grants = new[] { new { zoneId = zone.Id, roleId = role.Id } },
        }));

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal(1, await api.QueryAsync(db => db.Accounts.CountAsync(a => a.Username == username)));
        Assert.Equal(0, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "Account.OwnerRightsRefused")));
    }

    // On SQL Server a real edit changes the row version: the version read before it is then stale for end and reinstate.
    [SqlServerFact]
    public async Task A_real_edit_makes_the_old_row_version_stale_for_end_and_reinstate()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var name = $"NV {NewCode()}";
        var phone = NewPhone();
        var saved = await CreateAsync(owner, Profile(name, phone, zone.Id));
        var id = IdOf(saved);
        var oldVersion = RowVersionOf(saved);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)).ToString("yyyy-MM-dd");

        var edited = await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, zone.Id, b =>
        {
            b["notes"] = "QA edit";
            b["rowVersion"] = oldVersion;
        }));
        var newVersion = RowVersionOf(await edited.Content.ReadFromJsonAsync<JsonElement>());
        var staleEnd = await owner.PostAsJsonAsync($"/api/employees/{id}/end-employment", new { endDate = today, rowVersion = oldVersion });
        var ended = await owner.PostAsJsonAsync($"/api/employees/{id}/end-employment", new { endDate = today, rowVersion = newVersion });
        var staleReinstate = await owner.PostAsJsonAsync($"/api/employees/{id}/reinstate", new { rowVersion = oldVersion });
        var reinstated = await owner.PostAsync($"/api/employees/{id}/reinstate", null);

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.NotEqual(oldVersion, newVersion);
        Assert.Equal("MSG06", await staleEnd.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, ended.StatusCode);
        Assert.Equal("MSG06", await staleReinstate.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, reinstated.StatusCode);
    }

    // Blob storage (production provider) behind the same URLs: replaced and removed logos keep their URLs.
    [AzuriteFact]
    public async Task Logo_urls_keep_serving_replaced_and_removed_files_from_blob_storage()
    {
        using var api = new BlobLogoApiFactory(Environment.GetEnvironmentVariable(AzuriteFactAttribute.VariableName)!);
        await api.EnsureCooperativeAsync();
        var owner = await api.LoginAsync((await api.AddAccountAsync(NewCode(), Password)).Username);
        var first = await LogoUrlAsync(await UploadAsync(owner, Png, "image/png", "logo.png"));
        var second = await LogoUrlAsync(await UploadAsync(owner, PngOther, "image/png", "logo.png"));
        var removed = await owner.DeleteAsync("/api/cooperative/profile/logo");
        var anonymous = api.CreateClient();

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(Png, await anonymous.GetByteArrayAsync(first));
        Assert.Equal(PngOther, await anonymous.GetByteArrayAsync(second));
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/public/cooperative/logo")).StatusCode);
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient owner, byte[] content, string contentType, string fileName)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        return owner.PostAsync("/api/cooperative/profile/logo", form);
    }

    private static async Task<string> LogoUrlAsync(HttpResponseMessage uploaded)
    {
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        return (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("logoUrl").GetString()!;
    }

    private static ValidatedFile Validated(byte[] content)
    {
        var result = FileValidator.Validate([new IncomingFile("photo.png", content.Length, new MemoryStream(content))], FileRules.Photos);
        Assert.True(result.IsValid);
        return result.Files[0];
    }

    /// <summary>The same host as <see cref="AuthApiFactory"/> with the Azure Blob provider (Azurite) in place of Local.</summary>
    private sealed class BlobLogoApiFactory(string connectionString) : AuthApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Storage:Provider", "AzureBlob");
            builder.UseSetting("Storage:AzureBlob:ConnectionString", connectionString);
            builder.UseSetting("Storage:AzureBlob:Container", $"omts-qa-{Guid.NewGuid():N}");
        }
    }
}
