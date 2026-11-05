using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Employees.EmployeesV2QaKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>QA of OMTS-227 on a real SQL Server: malformed input is 4xx, the Zone Manager's list, the job and the bounds of the columns.</summary>
public class EmployeesV2QaInputSqlServerTests
{
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [SqlServerFact]
    public async Task Malformed_bodies_on_every_route_answer_4xx()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var saved = await CreateAsync(owner, Profile($"Input {NewCode()}", NewPhone(), zone.Id));
        var id = IdOf(saved);
        var version = RowVersionOf(saved);
        var bodies = new[]
        {
            string.Empty, "null", "[]", "1", "{", "{\"fullName\":5}", "{\"workingZoneIds\":\"abc\"}", "{\"workingZoneIds\":[null]}", "{\"workingZoneIds\":[\"not-a-guid\"]}",
            "{\"employeeType\":{}}", "{\"employeeTypeFrom\":\"31/12/2026\"}", "{\"birthday\":\"0000-00-00\"}", "{\"bankAccount\":\"x\"}", "{\"ownerApproved\":\"maybe\"}",
            "{\"account\":{}}", "{\"account\":{\"username\":null}}", "{\"account\":{\"username\":\"abc\",\"subjectType\":99}}", "{\"account\":{\"username\":\"abc\",\"grants\":[{}]}}",
            "{\"account\":{\"username\":\"abc\",\"grants\":[null]}}", "{\"lastWorkingDay\":\"yesterday\"}", "{\"rowVersion\":5}", "{\"rowVersion\":\"###\"}", "{\"zoneId\":\"abc\"}", "{\"zoneId\":5}",
            "{\"phoneNumber\":5}",
        };
        var routes = new (HttpMethod Method, string Url)[]
        {
            (HttpMethod.Post, "/api/employees"),
            (HttpMethod.Put, $"/api/employees/{id}"),
            (HttpMethod.Post, $"/api/employees/{id}/working-zones"),
            (HttpMethod.Post, $"/api/employees/{id}/end-employment"),
            (HttpMethod.Post, $"/api/employees/{id}/resume"),
            (HttpMethod.Post, $"/api/employees/{id}/account"),
        };

        foreach (var (method, url) in routes)
        {
            foreach (var body in bodies)
            {
                var response = await owner.SendAsync(new HttpRequestMessage(method, url) { Content = Json(body) });
                Assert.True((int)response.StatusCode is >= 400 and < 500, $"{method} {url} with {body}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            }
        }

        Assert.Equal(version, await CurrentVersionAsync(owner, id));
    }

    // A page beyond the int range is no reason for a server error: (page - 1) * pageSize overflows into a negative OFFSET.
    [SqlServerFact]
    public async Task L4_any_page_number_answers_200()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        await AddZoneAsync(api, cooperative.Id);
        var failures = new List<string>();

        foreach (var (page, size) in new[] { (2147483647, 100), (2147483647, 20), (1073741825, 2), (21474837, 100), (0, 0), (-5, -5) })
        {
            var response = await owner.GetAsync($"/api/employees?page={page}&pageSize={size}");
            if (response.StatusCode != HttpStatusCode.OK)
            {
                failures.Add($"page={page}&pageSize={size}: {(int)response.StatusCode}");
            }
        }

        Assert.Empty(failures);
    }

    [SqlServerFact]
    public async Task Odd_query_values_answer_200_or_4xx_and_like_wildcards_match_literally()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        await CreateAsync(owner, Profile($"Wild {NewCode()}", NewPhone(), zone.Id));

        foreach (var query in new[] { "search=%25", "search=_", "search=%5B", "search=%00", "statusId=999999", "statusId=abc", "zoneId=abc", $"zoneId={Guid.NewGuid()}" })
        {
            var response = await owner.GetAsync($"/api/employees?{query}");
            Assert.True((int)response.StatusCode < 500, $"{query}: {(int)response.StatusCode}");
            if (query is "search=%25" or "search=_" or "search=%5B")
            {
                Assert.Equal(0, (await response.JsonAsync(HttpStatusCode.OK)).GetProperty("total").GetInt32());
            }
        }
    }

    [SqlServerFact]
    public async Task L4_a_long_search_text_answers_200_or_4xx()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var failures = new List<string>();

        foreach (var length in new[] { 100, 201, 1000, 3999, 4001, 4100, 8000, 16000 })
        {
            var response = await owner.GetAsync($"/api/employees?search={new string('x', length)}");
            if ((int)response.StatusCode >= 500)
            {
                failures.Add($"{length}: {(int)response.StatusCode}");
            }
        }

        var cause = api.Logs.Lines.LastOrDefault(l => l.Contains("Exception", StringComparison.Ordinal));
        Assert.True(failures.Count == 0, $"{string.Join(", ", failures)}. {cause?[..Math.Min(400, cause.Length)]}");
    }

    // BR-LABOR-01 on the real provider: a number that is no type also reads back on every route.
    [SqlServerFact]
    public async Task M3_a_number_that_is_no_employee_type_stays_out_of_the_database()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var name = $"Enum {NewCode()}";

        await owner.PostAsJsonAsync("/api/employees", Profile(name, NewPhone(), zone.Id).With("employeeType", 99));
        await owner.PostAsJsonAsync("/api/employees", Profile($"{name} text", NewPhone(), zone.Id).With("employeeType", "99"));

        var stored = await database.CreateContext().Database.SqlQueryRaw<string>("SELECT DISTINCT [Type] AS [Value] FROM EmployeeTypePeriods").ToListAsync();
        var list = await (await owner.GetAsync($"/api/employees?search={Uri.EscapeDataString(name)}")).JsonAsync(HttpStatusCode.OK);
        Assert.Empty(stored);
        Assert.Equal(0, list.GetProperty("total").GetInt32());
    }

    [SqlServerFact]
    public async Task Extreme_dates_and_the_longest_account_fields_fit_the_columns()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);

        var first = await CreateAsync(owner, Profile($"Min {NewCode()}", NewPhone(), zone.Id).With("employeeTypeFrom", "0001-01-01").With("birthday", "0001-01-01"));
        await CreateAsync(owner, Profile($"Max {NewCode()}", NewPhone(), zone.Id).With("employeeTypeFrom", "9999-12-31"));
        var ended = await (await owner.PostEndAsync(IdOf(first), new { lastWorkingDay = "0001-01-01", rowVersion = RowVersionOf(first) })).JsonAsync(HttpStatusCode.OK);
        Assert.Equal("Left", ended.GetProperty("employee").GetProperty("status").GetString());

        var email = new string('a', 256 - "@example.test".Length) + "@example.test";
        var longName = new string('N', 100);
        var username = new string('n', 49) + "a";
        var account = await owner.PostAsJsonAsync("/api/employees", Profile(longName, NewPhone(), zone.Id).With("account", new { username, email }));
        Assert.Equal(HttpStatusCode.Created, account.StatusCode);
        Assert.Equal(longName, await QueryAsync(api, db => db.Accounts.Where(a => a.Username == username).Select(a => a.DisplayName).SingleAsync()));

        foreach (var (name, block) in new (string, object)[]
        {
            ($"Long {NewCode()}", new { username = new string('n', 51) }),
            ($"Long {NewCode()}", new { username = $"nv{NewCode()}", email = new string('a', 300) + "@example.test" }),
            (new string('N', 101), new { username = $"nv{NewCode()}" }),
        })
        {
            var refused = await owner.PostAsJsonAsync("/api/employees", Profile(name, NewPhone(), zone.Id).With("account", block));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        var manyZones = await owner.PostAsJsonAsync("/api/employees", Profile($"Zones {NewCode()}", NewPhone(), zone.Id).With("workingZoneIds", Enumerable.Range(0, 3000).Select(_ => Guid.NewGuid()).ToArray()));
        Assert.Equal(("MSG-GEN-01", "Working zones"), (await manyZones.ErrorCodeAsync(), await FieldAsync(manyZones)));
    }

    // SCR-LABOR-01 on the real provider: the ordering by the caller's zones with the split query and paging; a Zone Manager's list.
    [SqlServerFact]
    public async Task A_managers_list_pages_with_their_zones_first_on_sql_server()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var (zoneA, zoneB) = (await AddZoneAsync(api, cooperative.Id), await AddZoneAsync(api, cooperative.Id));
        var tag = NewCode();
        var ids = new List<Guid>();
        foreach (var (name, zone) in new[] { ("A", zoneB), ("B", zoneA), ("C", zoneB), ("D", zoneA) })
        {
            ids.Add(IdOf(await CreateAsync(owner, Profile($"{tag} {name}", NewPhone(), zone.Id))));
        }

        var manager = await ManagerOnSqlAsync(api, zoneA);
        var first = await (await manager.GetAsync($"/api/employees?search={tag}&pageSize=2")).JsonAsync(HttpStatusCode.OK);
        var second = await (await manager.GetAsync($"/api/employees?search={tag}&pageSize=2&page=2")).JsonAsync(HttpStatusCode.OK);
        var filtered = await (await manager.GetAsync($"/api/employees?zoneId={zoneB.Id}&statusId={StatusIds.Employee.Working}")).JsonAsync(HttpStatusCode.OK);

        Assert.Equal([ids[1], ids[3]], first.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal([ids[0], ids[2]], second.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal(4, first.GetProperty("total").GetInt32());
        Assert.Equal(2, filtered.GetProperty("total").GetInt32());
        Assert.All(second.GetProperty("items").EnumerateArray(), i => Assert.False(i.GetProperty("canMaintain").GetBoolean()));
    }

    [SqlServerFact]
    public async Task The_job_runs_on_sql_server_with_no_person_and_at_start_when_it_is_late()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var today = Today();
        var phone = NewPhone();
        var byJob = IdOf(await CreateAsync(owner, Profile($"Job {NewCode()}", phone, zone.Id)));
        await EndAsync(owner, byJob, today);

        Assert.Equal(0, await api.Services.GetRequiredService<EmploymentEndJob>().RunOnceAsync(today));
        Assert.Equal(1, await api.Services.GetRequiredService<EmploymentEndJob>().RunOnceAsync(today.AddDays(1)));
        var stored = await QueryAsync(api, db => db.Employees.AsNoTracking().SingleAsync(e => e.Id == byJob));
        var row = await QueryAsync(api, db => db.AuditEvents.AsNoTracking().SingleAsync(e => e.RecordId == byJob && e.Action == "Employee.StatusChanged"));
        Assert.Equal(StatusIds.Employee.Left, stored.StatusId);
        Assert.Null(row.ActorAccountId);
        Assert.Null(row.ActorEmployeeId);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/employees", Profile($"Free {NewCode()}", phone, zone.Id))).StatusCode);

        // A host that starts late runs the job once by itself (CatchUpOnStart, the default): a day already over goes to Left.
        var late = IdOf(await CreateAsync(owner, Profile($"Late {NewCode()}", NewPhone(), zone.Id)));
        await ChangeAsync(api, db => db.Employees.Where(e => e.Id == late).ExecuteUpdate(s => s.SetProperty(e => e.LastWorkingDay, today.AddDays(-2))));
        await using var restarted = new SqlServerApiFactory(database).WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Jobs:EmploymentEnd:CatchUpOnStart"] = "true", ["Jobs:EmploymentEnd:RunAt"] = "00:00" })));
        _ = restarted.CreateClient();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var status = StatusIds.Employee.Working;
        while (status == StatusIds.Employee.Working && DateTime.UtcNow < deadline)
        {
            status = await QueryAsync(api, db => db.Employees.Where(e => e.Id == late).Select(e => e.StatusId).SingleAsync());
        }

        Assert.Equal(StatusIds.Employee.Left, status);
    }

    // BR-ACCOUNT-02 where the column has a unique index only for verified emails.
    [SqlServerFact]
    public async Task M2_an_email_verified_on_another_account_answers_msg_account_09_and_not_a_bare_duplicate()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var email = $"{NewCode()}@example.test";
        var other = await api.AddAccountAsync(NewCode(), NewPassword(), SubjectType.Staff);
        await ChangeAsync(api, db => db.Accounts.Where(a => a.Id == other.Id).ExecuteUpdate(s => s.SetProperty(a => a.Email, email).SetProperty(a => a.EmailVerifiedAt, DateTimeOffset.UtcNow)));
        var name = $"Email {NewCode()}";

        var response = await owner.PostAsJsonAsync("/api/employees", Profile(name, NewPhone(), zone.Id).With("account", new { username = $"nv{NewCode()}", email }));

        Assert.Equal("MSG-ACCOUNT-09", await response.ErrorCodeAsync());
        Assert.Equal(0, await QueryAsync(api, db => db.Employees.CountAsync(e => e.FullName == name)));
    }

    // BR-LABOR-01: the number in other digits against a holder of the same number. Full-width digits compare equal on the collation; these two do not.
    [SqlServerFact]
    public async Task M4_a_number_in_other_digits_cannot_give_two_employed_employees_one_number()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var results = new List<string>();

        foreach (var zero in new[] { '\uFF10', '\u0660', '\u0966' })
        {
            var phone = NewPhone();
            await CreateAsync(owner, Profile($"Holder {NewCode()}", phone, zone.Id));

            var response = await owner.PostAsJsonAsync("/api/employees", Profile($"Twin {NewCode()}", "0" + InDigits(phone[1..], zero), zone.Id));

            results.Add($"U+{(int)zero:X4}: {(int)response.StatusCode}");
        }

        Assert.All(results, r => Assert.DoesNotContain("201", r));
    }

    // Vietnamese numbers are 10 ASCII digits (PhoneNumbers); \d in .NET also takes every other Unicode digit, which is then stored.
    [SqlServerFact]
    public async Task M4_other_unicode_digits_are_not_a_phone_number()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var results = new List<string>();

        foreach (var zero in new[] { '\uFF10', '\u0660', '\u0966' })
        {
            var response = await owner.PostAsJsonAsync("/api/employees", Profile($"Unicode {NewCode()}", "0" + InDigits(NewPhone()[1..], zero), zone.Id));
            results.Add($"U+{(int)zero:X4}: {(int)response.StatusCode} {await response.ErrorCodeAsync()}");
        }

        Assert.All(results, r => Assert.Contains("400 MSG-GEN-01", r));
    }

    // LABOR-01.5 step 4, GBR-02, NFR-01: the password in the answer only, once, on the real provider and with the host's log.
    [SqlServerFact]
    public async Task The_temporary_password_is_in_the_answer_only()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var username = $"nv{NewCode()}";

        var saved = await CreateAsync(owner, Profile($"Secret {NewCode()}", NewPhone(), zone.Id).With("account", new { username }));
        var temporary = saved.GetProperty("temporaryPassword").GetString()!;
        var stored = await QueryAsync(api, db => db.Accounts.Where(a => a.Username == username).Select(a => a.PasswordHash).SingleAsync());
        var rows = await QueryAsync(api, db => db.AuditEvents.Select(e => (e.Before ?? string.Empty) + (e.After ?? string.Empty) + (e.Reason ?? string.Empty)).ToListAsync());
        var reread = await owner.GetAsync($"/api/employees/{IdOf(saved)}");

        Assert.NotEqual(temporary, stored);
        Assert.DoesNotContain(rows, r => r.Contains(temporary) || r.Contains(stored!));
        Assert.DoesNotContain(api.Logs.Lines, l => l.Contains(temporary));
        Assert.DoesNotContain(temporary, await reread.Content.ReadAsStringAsync());
    }

    private static async Task<HttpClient> ManagerOnSqlAsync(SqlServerApiFactory api, OMTS.Domain.Entities.Farming.FarmingZone zone)
    {
        var password = NewPassword();
        var account = await api.AddAccountAsync(NewCode(), password, SubjectType.Staff);
        await ChangeAsync(api, db => db.Accounts.Where(a => a.Id == account.Id).ExecuteUpdate(s => s.SetProperty(a => a.PhoneNumber, NewPhone()).SetProperty(a => a.PhoneVerifiedAt, DateTimeOffset.UtcNow)));
        await api.GrantAsync(account, StaffRole.ZoneManager, zone.Id);
        var client = api.CreateClient();
        var tokens = await LoginTokensAsync(client, account.Username, password);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }
}
