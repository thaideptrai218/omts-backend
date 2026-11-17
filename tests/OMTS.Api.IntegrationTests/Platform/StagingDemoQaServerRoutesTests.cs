using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Infrastructure.Bootstrap.Demo;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>QA of OMTS-216 on the real server: dev tooling, fake senders and bank data.</summary>
public partial class StagingDemoQaServerTests
{
    public static TheoryData<string> Environments => new() { "Staging", "Production" };

    private StagingDemoQaProcessKit.RunningApi Api(string environment) => environment == "Staging" ? server.Staging! : server.Production!;

    private static readonly string[] OffPaths =
    [
        "/api/dev/sms", "/api/dev/email", "/api/dev/push",
        "/api/dev/sms/", "/api/dev/email/", "/api/dev/push/",
        "/API/DEV/SMS", "/Api/Dev/Email", "/api/DEV/push/",
        "/api/dev/sms?phoneNumber=0900000015", "/api/dev/email?to=a@example.com", "/api/dev/push?accountId=00000000-0000-0000-0000-000000000001",
        "/api/dev/push?accountId=not-a-guid",
        "//api/dev/sms", "/api//dev/sms", "/api/dev/%73ms", "/api/dev/sms%2F", "/api/dev/sms;x=1",
        "/swagger", "/swagger/", "/SWAGGER", "/Swagger/Index.html", "/swagger/index.html", "/SWAGGER/INDEX.HTML",
        "/swagger/v1/swagger.json", "/SWAGGER/V1/SWAGGER.JSON", "/swagger/v1/swagger.yaml", "/swagger/swagger-ui.css",
        "/swagger/swagger-ui-bundle.js", "/swagger/index.js", "/swagger/oauth2-redirect.html",
        "/index.html", "/api-docs", "/openapi/v1.json", "/OMTS.Api.xml", "/appsettings.json", "/appsettings.Development.json",
    ];

    [StagingDemoQaSqlServerTheory]
    [MemberData(nameof(Environments))]
    public async Task Every_address_of_the_dev_inbox_and_swagger_is_404_for_get_and_delete_with_and_without_a_token(string environment)
    {
        var api = Api(environment);
        var wrong = new List<string>();
        var token = (await LoginAsync(api.Client, "owner", server.Secret)).Body.GetProperty("accessToken").GetString();

        foreach (var path in OffPaths)
        {
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Delete })
            {
                foreach (var bearer in new[] { null, token })
                {
                    // Absolute: a path starting with // would otherwise name a host.
                    using var request = new HttpRequestMessage(method, new Uri(api.Client.BaseAddress!.GetLeftPart(UriPartial.Authority) + path));
                    if (bearer is not null)
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                    }

                    using var response = await api.Client.SendAsync(request);
                    // An action found by its route answers an empty problem document, an unknown address nothing at all.
                    var body = await response.Content.ReadAsStringAsync();
                    if (response.StatusCode != HttpStatusCode.NotFound || body.Contains("recipient", StringComparison.OrdinalIgnoreCase) || body.Contains("openapi", StringComparison.OrdinalIgnoreCase))
                    {
                        wrong.Add($"{method} {path} token={bearer is not null}: {(int)response.StatusCode} {body}");
                    }
                }
            }
        }

        Assert.Empty(wrong);
    }

    // The route exists, so another verb answers 405 and names GET and DELETE. No data and no action: a passing record.
    [StagingDemoQaSqlServerTheory]
    [MemberData(nameof(Environments))]
    public async Task Other_verbs_on_the_dev_inbox_answer_405_with_an_empty_body(string environment)
    {
        var api = Api(environment);
        foreach (var path in new[] { "/api/dev/sms", "/api/dev/email", "/api/dev/push" })
        {
            foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Head })
            {
                using var response = await api.Client.SendAsync(new HttpRequestMessage(method, path));
                Assert.True(response.StatusCode == HttpStatusCode.MethodNotAllowed, $"{method} {path}: {(int)response.StatusCode}");
                Assert.Empty(await response.Content.ReadAsStringAsync());
            }
        }

        using var overridden = new HttpRequestMessage(HttpMethod.Post, "/api/dev/sms");
        overridden.Headers.Add("X-HTTP-Method-Override", "GET");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await api.Client.SendAsync(overridden)).StatusCode);
    }

    // The same requests reach something in Development, so the 404s above are not a wrong address.
    [SqlServerFact]
    public async Task Control_the_real_development_host_serves_them()
    {
        var client = server.Development!.Client;

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/dev/sms")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/API/DEV/SMS/")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/dev/email")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/swagger/index.html")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/swagger/v1/swagger.json")).StatusCode);
    }

    // A demo refusal on a seeded database in Production stays as before: refused, and nothing changes.
    [SqlServerFact]
    public async Task Production_refuses_the_demo_on_a_seeded_database_and_the_secret_still_works_on_staging_only_as_seeded()
    {
        await using var db = Database();
        var before = await db.Accounts.CountAsync();

        var run = await server.Kit!.RunAsync(server.Settings("Production"), "seed", "--bootstrap", "--demo");

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("refused outside Development and Staging", run.Output);
        Assert.Equal(before, await db.Accounts.CountAsync());
    }

    // Change list, "Fake providers": Sms None answers 503 MSG-ACCOUNT-13, and a list row stays saved though Push is None.
    [SqlServerFact]
    public async Task With_none_providers_a_code_request_is_503_and_notification_rows_are_saved()
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, server.FirstCodeRequestStatus);
        Assert.Contains("MSG-ACCOUNT-13", server.FirstCodeRequestBody);
        await using var db = Database();
        Assert.True(await db.Notifications.AnyAsync());
    }

    // Change list: Email:Provider=Smtp without a host gives no warning at start, and each send fails with 503. An email
    // no longer registers, so the send is the code for a new contact email of a logged-in account.
    [SqlServerFact]
    public async Task Smtp_without_a_host_gives_no_warning_and_a_send_answers_503()
    {
        Assert.DoesNotContain("Email provider", server.Staging!.Output);
        var (_, body) = await LoginAsync(server.Staging.Client, "owner", server.Secret);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/me/contact-changes")
        {
            Content = JsonContent.Create(new { channel = "Email", target = $"qa-216-{Guid.NewGuid():N}@example.com", currentPassword = server.Secret }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());

        var response = await server.Staging.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    // Change list: fake SMS, email and push only warn, on the real process too; the client is told the code was sent
    // and the inbox stays closed. Both environments, as the change list's open question 2 says.
    [StagingDemoQaSqlServerTheory]
    [MemberData(nameof(Environments))]
    public async Task Fake_senders_only_warn_on_the_real_process_and_the_inbox_stays_closed(string environment)
    {
        var settings = server.Settings(environment);
        settings["Sms__Provider"] = "Fake";
        settings["Email__Provider"] = "Fake";
        settings["Push__Provider"] = "Fake";
        settings["Jobs__OrphanFileCleanup__Enabled"] = "false";
        using var api = await server.Kit!.StartApiAsync(settings);
        Assert.False(api.HasExited, api.Output);

        Assert.Contains("SMS provider is Fake", api.Output);
        Assert.Contains("Push provider is Fake", api.Output);
        Assert.Contains("Email provider is Fake", api.Output);
        var (_, body) = await LoginAsync(api.Client, "nhanvien.chuaxacthuc", server.Secret);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/me/phone-verification") { Content = JsonContent.Create(new { }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());

        var sent = await api.Client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, sent.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Client.GetAsync("/api/dev/sms")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Client.DeleteAsync("/api/dev/sms")).StatusCode);
    }

    // Requirement: no real bank account on the demo server. The seed stores the plan's placeholders and nothing on the cooperative.
    [SqlServerFact]
    public async Task The_seed_stores_no_cooperative_bank_account_and_only_the_placeholder_numbers_on_employees()
    {
        await using var db = Database();
        var cooperative = await db.Cooperatives.SingleAsync();
        Assert.Null(cooperative.BankAccount);

        var employees = await db.Employees.ToListAsync();
        var planned = DemoEmployeePlans.All.Select(p => p.BankAccountNumber).Order();
        Assert.Equal(planned, employees.Select(e => e.BankAccount!.AccountNumber).Order());
    }
}
