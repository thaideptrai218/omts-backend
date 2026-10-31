using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Cooperatives;

/// <summary>FARM-01.1 multipart form fields; <see cref="File"/> null sends no file.</summary>
public sealed record CertificationFields
{
    public string? Name { get; init; } = $"VietGAP {NewCode()}";
    public string? Number { get; init; } = $"VG-{NewCode()}";
    public string? Issuer { get; init; } = "Trung tâm Chứng nhận Phù hợp";
    public DateOnly? ExpiryDate { get; init; }
    public string? RowVersion { get; init; }
    public byte[]? File { get; init; } = FarmingTestKit.Pdf;
    public string FileName { get; init; } = "certificate.pdf";

    public MultipartFormDataContent ToForm()
    {
        var form = new MultipartFormDataContent();
        void Add(string? value, string name)
        {
            if (value is not null)
            {
                form.Add(new StringContent(value), name);
            }
        }

        Add(Name, "name");
        Add(Number, "number");
        Add(Issuer, "issuer");
        Add(ExpiryDate?.ToString("yyyy-MM-dd"), "expiryDate");
        Add(RowVersion, "rowVersion");
        if (File is not null)
        {
            form.Add(FarmingTestKit.FileContent(File), "file", FileName);
        }

        return form;
    }
}

/// <summary>Helpers of the FARM-01 v2 tests: a logged-in Owner, certification calls and a host whose clock stands still.</summary>
internal static class CooperativeCertificationRequests
{
    public const string Certifications = "/api/cooperative/certifications";

    /// <summary>The cooperative and a logged-in Owner (a fresh password, never a literal).</summary>
    public static async Task<HttpClient> NewOwnerAsync(this AuthApiFactory api)
    {
        await api.EnsureCooperativeAsync();
        var password = NewPassword();
        var owner = await api.AddAccountAsync(NewCode(), password);
        return await api.LoginAsync(owner.Username, password);
    }

    /// <summary>A logged-in account of another kind: staff with an All-zones pair of <paramref name="role"/>, or a trader.</summary>
    public static async Task<HttpClient> NonOwnerAsync(this AuthApiFactory api, SubjectType subjectType, StaffRole? role = null)
    {
        var password = NewPassword();
        var account = await api.AddAccountAsync(NewCode(), password, subjectType);
        if (role is { } staffRole)
        {
            await api.GrantAsync(account, staffRole, null);
        }

        return await api.LoginAsync(account.Username, password);
    }

    public static Task<HttpResponseMessage> AddCertificationAsync(this HttpClient owner, CertificationFields fields) =>
        owner.PostAsync(Certifications, fields.ToForm());

    /// <summary>Adds a certification and returns the saved certification (fails the test when it is not 201).</summary>
    public static async Task<System.Text.Json.JsonElement> CreateCertificationAsync(this HttpClient owner, CertificationFields? fields = null) =>
        (await (await owner.AddCertificationAsync(fields ?? new CertificationFields())).ReadAsync(HttpStatusCode.Created)).GetProperty("certification");

    public static Task<HttpResponseMessage> UploadCoverPhotoAsync(this HttpClient client, byte[] content, string fileName = "cover.png", string field = "file") =>
        client.PostAsync("/api/cooperative/profile/cover-photo", new MultipartFormDataContent { { FarmingTestKit.FileContent(content, "image/png"), field, fileName } });

    /// <summary>The in-memory database issues no row versions: stores one on the certification, as SQL Server would.</summary>
    public static Task<string> IssueCertificationVersionAsync(this AuthApiFactory api, Guid id) => api.IssueRowVersionAsync<CooperativeCertification>(id);
}

/// <summary>The API with a clock that stands still at <paramref name="now"/>, so "today" cannot move during a test.</summary>
public sealed class StillClockApiFactory(DateTimeOffset now) : AuthApiFactory
{
    /// <summary>The business date the server works with.</summary>
    public DateOnly Today { get; } = BusinessClock.ToBusinessDate(now);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new StillClock(now));
        });
    }

    private sealed class StillClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
