using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Cooperatives;

/// <summary>Review follow-ups of OMTS-199: immutable logo URLs, Owner-rights rule, duplicates, search, stale status actions.</summary>
public class CooperativeReviewFixTests
{
    private static readonly byte[] PngA = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0xA];
    private static readonly byte[] PngB = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0xB];

    // A published provenance version stores the logo URL: it must keep showing the logo of that time.
    [Fact]
    public async Task Every_logo_has_its_own_url_that_keeps_serving_that_logo()
    {
        using var api = new AuthApiFactory();
        await api.EnsureCooperativeAsync();
        var owner = await api.LoginAsync((await api.AddAccountAsync(NewCode(), Password)).Username);

        var firstUrl = await LogoUrlAsync(await UploadAsync(owner, PngA));
        var secondUrl = await LogoUrlAsync(await UploadAsync(owner, PngB));
        var anonymous = api.CreateClient();

        Assert.NotEqual(firstUrl, secondUrl);
        Assert.StartsWith("/api/public/cooperative/logos/", firstUrl);
        Assert.Equal(PngA, await anonymous.GetByteArrayAsync(firstUrl));
        Assert.Equal(PngB, await anonymous.GetByteArrayAsync(secondUrl));
        Assert.Equal(PngB, await anonymous.GetByteArrayAsync("/api/public/cooperative/logo"));
    }

    // Only logo files can be reached, never another path or category.
    [Theory]
    [InlineData("/api/public/cooperative/logos/2026/10/0123456789abcdef0123456789abcdef.pdf")]
    [InlineData("/api/public/cooperative/logos/2026/10/..%2F..%2Fevidence%2F2026%2F10%2F0123456789abcdef0123456789abcdef.png")]
    [InlineData("/api/public/cooperative/logos/26/10/0123456789abcdef0123456789abcdef.png")]
    [InlineData("/api/public/cooperative/logos/2026/10/0123456789abcdef0123456789abcdef.png")]
    public async Task Logo_file_url_reaches_nothing_but_stored_logo_files(string url)
    {
        using var api = new AuthApiFactory();

        Assert.Equal(HttpStatusCode.NotFound, (await api.CreateClient().GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Removing_a_logo_that_does_not_exist_changes_nothing_and_writes_no_audit_row()
    {
        using var api = new AuthApiFactory();
        await api.EnsureCooperativeAsync();
        var owner = await api.LoginAsync((await api.AddAccountAsync(NewCode(), Password)).Username);

        var removed = await owner.DeleteAsync("/api/cooperative/profile/logo");

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(0, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "Cooperative.LogoChanged")));
    }

    [Fact]
    public async Task Tax_code_with_non_ascii_digits_is_msg01()
    {
        using var api = new AuthApiFactory();
        await api.EnsureCooperativeAsync();
        var owner = await api.LoginAsync((await api.AddAccountAsync(NewCode(), Password)).Username);
        var profile = await owner.GetFromJsonAsync<JsonElement>("/api/cooperative/profile");

        var response = await owner.PutAsJsonAsync("/api/cooperative/profile", new
        {
            fullName = "Hợp tác xã Trung Nam",
            shortName = "HTX Trung Nam",
            taxCode = "٠١٢٣٤٥٦٧٨٩",
            officeAddress = "Vân Đồn, Quảng Ninh",
            phoneNumber = NewPhone(),
            legalRepresentative = "Nguyễn Văn A",
            businessLines = new[] { "Nuôi hàu" },
            rowVersion = profile.GetProperty("rowVersion").GetString(),
        });

        Assert.Equal("MSG-GEN-01", await response.ErrorCodeAsync());
        Assert.Equal("Tax code", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
    }

    // Two identical zone–role rows are reported as such (MSG-GEN-02 "Zone-role pair"), not as a taken username or phone.
    [Fact]
    public async Task Repeated_zone_role_row_in_a_new_employee_account_keeps_its_own_message()
    {
        using var api = new AuthApiFactory();
        var (owner, zone, role) = await SetupAsync(api);
        var body = Profile($"NV {NewCode()}", NewPhone(), zone.Id, b => b["account"] = new
        {
            username = $"u{NewCode()}",
            grants = new[] { new { zoneId = zone.Id, roleId = role.Id }, new { zoneId = zone.Id, roleId = role.Id } },
        });

        var response = await owner.PostAsJsonAsync("/api/employees", body);
        var detail = (await ProblemAsync(response)).GetProperty("detail").GetString();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.StartsWith("Zone-role pair already exists", detail);
    }

    [Fact]
    public async Task Search_by_phone_typed_with_the_country_code_finds_the_employee()
    {
        using var api = new AuthApiFactory();
        var (owner, zone, _) = await SetupAsync(api);
        var phone = NewPhone();
        var id = IdOf(await CreateAsync(owner, Profile($"NV {NewCode()}", phone, zone.Id)));

        var found = await owner.GetFromJsonAsync<JsonElement>($"/api/employees?search={Uri.EscapeDataString("+84" + phone[1..])}");

        Assert.Contains(found.GetProperty("items").EnumerateArray(), e => e.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task Status_actions_from_a_stale_screen_are_msg06_and_a_future_end_date_is_msg01()
    {
        using var api = new AuthApiFactory();
        var (owner, zone, _) = await SetupAsync(api);
        var saved = await CreateAsync(owner, Profile($"NV {NewCode()}", NewPhone(), zone.Id));
        var id = IdOf(saved);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));

        var future = await owner.PostAsJsonAsync($"/api/employees/{id}/end-employment", new { endDate = today.AddDays(2).ToString("yyyy-MM-dd") });
        var stale = await owner.PostAsJsonAsync($"/api/employees/{id}/end-employment", new { endDate = today.ToString("yyyy-MM-dd"), rowVersion = "AAAAAAAAB9E=" });
        var ended = await owner.PostAsJsonAsync($"/api/employees/{id}/end-employment", new { endDate = today.ToString("yyyy-MM-dd"), rowVersion = RowVersionOf(saved) });
        // The in-memory provider issues no new row versions: a version the server never issued stands for a stale screen.
        var staleReinstate = await owner.PostAsJsonAsync($"/api/employees/{id}/reinstate", new { rowVersion = "AAAAAAAAB9E=" });
        var reinstated = await owner.PostAsync($"/api/employees/{id}/reinstate", null);

        Assert.Equal("End date", await FieldAsync(future));
        Assert.Equal("MSG06", await stale.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, ended.StatusCode);
        Assert.Equal("MSG06", await staleReinstate.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, reinstated.StatusCode);
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient owner, byte[] content)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "logo.png");
        return owner.PostAsync("/api/cooperative/profile/logo", form);
    }

    private static async Task<string> LogoUrlAsync(HttpResponseMessage uploaded)
    {
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        return (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("logoUrl").GetString()!;
    }
}
