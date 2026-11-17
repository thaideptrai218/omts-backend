using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OMTS.Api.IntegrationTests.Employees;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Constants;
using OMTS.Infrastructure.Identity;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Employees.EmployeesV2QaKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.EmployeeAppKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// QA red team of L3 (LABOR-03, LABOR-06, BR-LABOR-02). Tests whose name starts with "Finding_" assert the behaviour QA recommends and
/// fail on the code as it is today (see the QA report qa-261111-l3-employee-app-auth); the others are probes expected to pass.
/// </summary>
public class EmployeeAppQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static async Task<(JsonElement Saved, Worker Worker)> CreateWorkerAsync(World world)
    {
        var (name, phone) = ($"Worker {NewCode()}", NewPhone());
        var saved = await CreateAsync(world.Owner, Profile(name, phone, world.Zone.Id));
        return (saved, new Worker(IdOf(saved), phone, name));
    }

    private static async Task<(HttpStatusCode Status, string? Code, string? Detail)> RefusalAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (
            response.StatusCode,
            body.TryGetProperty("code", out var code) ? code.GetString() : null,
            body.TryGetProperty("detail", out var detail) ? detail.GetString() : null);
    }

    // Finding L1: a code issued before a phone number change still links a device on the new number.
    [Fact]
    public async Task Finding_an_open_code_is_cancelled_when_the_phone_number_changes()
    {
        var world = await WorldAsync(factory);
        var (saved, worker) = await CreateWorkerAsync(world);
        var code = await IssueCodeAsync(world.Manager, worker.Id);
        var newPhone = NewPhone();

        await (await world.Owner.PutAsJsonAsync($"/api/employees/{worker.Id}",
            Profile(worker.Name, newPhone, world.Zone.Id).With("rowVersion", RowVersionOf(saved)))).JsonAsync(HttpStatusCode.OK);

        var login = await LoginWithCodeAsync(factory.CreateClient(), newPhone, code);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal("MSG-LABOR-19", await login.ErrorCodeAsync());
        Assert.All(await factory.CodesAsync(worker.Id), c => Assert.False(c.ConsumedAt is null && c.CancelledAt is null));
    }

    // Finding L1: a code issued before an end of employment works again after a resume within its 5 minutes.
    [Fact]
    public async Task Finding_an_open_code_does_not_survive_an_end_of_employment_and_a_resume()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var code = await IssueCodeAsync(world.Manager, worker.Id);
        await EndAsync(world.Owner, worker.Id);
        var resumed = await world.Owner.PostAsJsonAsync($"/api/employees/{worker.Id}/resume", new { rowVersion = await CurrentVersionAsync(world.Owner, worker.Id) });
        await resumed.JsonAsync(HttpStatusCode.OK);

        var login = await LoginWithCodeAsync(factory.CreateClient(), worker.Phone, code);

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal("MSG-LABOR-19", await login.ErrorCodeAsync());
    }

    // Finding M1: at the production limit (10 per minute per IP, shared with the anonymous SMS-code endpoints) the 11th code of a crew is 429.
    [Fact]
    public async Task Finding_one_issuer_gets_codes_for_a_crew_of_eleven_in_one_minute_at_the_default_limit()
    {
        var world = await WorldAsync(factory);
        var crew = new List<Worker>();
        for (var i = 0; i < 11; i++)
        {
            crew.Add(await AddWorkerAsync(world));
        }

        var password = NewPassword();
        var account = await factory.AddAccountAsync(NewCode(), password);
        using var api = factory.WithWebHostBuilder(b => b.UseSetting("RateLimits:CodeRequestsPerMinute", "10"));
        var issuer = WithBearer(api.CreateClient(), (await LoginTokensAsync(api.CreateClient(), account.Username, password)).GetProperty("accessToken").GetString()!);

        var statuses = new List<HttpStatusCode>();
        foreach (var worker in crew)
        {
            statuses.Add((await issuer.PostAsync($"/api/employees/{worker.Id}/login-codes", null)).StatusCode);
        }

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.Created, s));
    }

    // Finding L2: the probe documented today's shared budget (a 429 here). The fix splits the policies, so the probe now asserts the recommendation:
    // staff logins no longer use up the employee app's per-IP budget (its own limit is tested in EmployeeAppPushAndLimitTests).
    [Fact]
    public async Task Finding_staff_logins_do_not_use_up_the_employee_app_per_ip_budget()
    {
        using var api = factory.WithWebHostBuilder(b => b.UseSetting("RateLimits:LoginsPerMinute", "2"));
        var client = api.CreateClient();
        for (var i = 0; i < 2; i++)
        {
            var staff = await client.PostAsJsonAsync("/api/auth/login", new { identifier = NewCode(), password = NewPassword() });
            Assert.NotEqual(HttpStatusCode.TooManyRequests, staff.StatusCode);
        }

        var employee = await client.PostAsJsonAsync(Login, new { phoneNumber = NewPhone() });

        Assert.Equal(HttpStatusCode.Unauthorized, employee.StatusCode);
    }

    // MSG-LABOR-19: no code, a wrong code and a spent code answer alike, so the answer tells nothing about which.
    [Fact]
    public async Task A_wrong_a_spent_and_a_never_issued_code_get_the_same_answer()
    {
        var world = await WorldAsync(factory);
        var neverIssued = await AddWorkerAsync(world);
        var wrong = await AddWorkerAsync(world);
        var spent = await AddWorkerAsync(world);
        var wrongCode = await IssueCodeAsync(world.Manager, wrong.Id);
        var spentCode = await IssueCodeAsync(world.Manager, spent.Id);
        await (await LoginWithCodeAsync(factory.CreateClient(), spent.Phone, spentCode)).JsonAsync(HttpStatusCode.OK);

        var answers = new[]
        {
            await RefusalAsync(await LoginWithCodeAsync(factory.CreateClient(), neverIssued.Phone, "123456")),
            await RefusalAsync(await LoginWithCodeAsync(factory.CreateClient(), wrong.Phone, wrongCode == "123456" ? "654321" : "123456")),
            await RefusalAsync(await LoginWithCodeAsync(factory.CreateClient(), spent.Phone, spentCode)),
        };

        Assert.All(answers, a => Assert.Equal((HttpStatusCode.Unauthorized, "MSG-LABOR-19"), (a.Status, a.Code)));
        Assert.Single(answers.Select(a => a.Detail).Distinct());
    }

    // The device id and secret of one employee are nothing to another employee's phone number.
    [Fact]
    public async Task The_device_secret_of_one_employee_never_logs_in_another()
    {
        var world = await WorldAsync(factory);
        var first = await AddWorkerAsync(world);
        var other = await AddWorkerAsync(world);
        var linked = await LinkDeviceAsync(factory, world, first);

        var borrowed = await LoginOnDeviceAsync(factory.CreateClient(), other.Phone, linked);

        Assert.Equal(HttpStatusCode.Unauthorized, borrowed.StatusCode);
        Assert.Equal("MSG-LABOR-17", await borrowed.ErrorCodeAsync());
        Assert.Empty(await factory.SessionsAsync(other.Id));
        Assert.True((await factory.DeviceAsync(linked)).IsLinked);
    }

    // The client-known QR key is never a key either scheme accepts a token from.
    [Fact]
    public async Task A_token_signed_with_the_qr_key_the_app_holds_is_refused_by_both_schemes()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var linked = await LinkDeviceAsync(factory, world, worker);
        var session = Assert.Single(await factory.SessionsAsync(worker.Id));
        var jwt = factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
        var qrKey = new SymmetricSecurityKey(Base64UrlEncoder.DecodeBytes(linked.GetProperty("qrKey").GetString()!));
        var claims = new Dictionary<string, object>
        {
            [OmtsClaimTypes.EmployeeId] = worker.Id.ToString(),
            [OmtsClaimTypes.EmployeeSessionId] = session.Id.ToString(),
            [OmtsClaimTypes.EmployeeDeviceId] = linked.GetProperty("deviceId").GetGuid().ToString(),
            [OmtsClaimTypes.TokenId] = Guid.NewGuid().ToString(),
            [OmtsClaimTypes.Name] = "Forged",
            [OmtsClaimTypes.SubjectType] = OmtsClaimTypes.EmployeeSubjectType,
        };

        foreach (var audience in new[] { jwt.EmployeeAudience, jwt.Audience })
        {
            var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = jwt.Issuer,
                Audience = audience,
                NotBefore = DateTime.UtcNow.AddMinutes(-1),
                Expires = DateTime.UtcNow.AddMinutes(10),
                SigningCredentials = new SigningCredentials(qrKey, SecurityAlgorithms.HmacSha256),
                Claims = claims,
            });
            var client = WithBearer(factory.CreateClient(), token);

            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(Logout, null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
        }

        // The real token still works, so the refusals above are the forgery's doing.
        Assert.Equal(HttpStatusCode.NoContent, (await factory.AsEmployee(linked).PutAsJsonAsync(PushToken, new { pushToken = (string?)null, pushPermissionGranted = false })).StatusCode);
    }

    // GBR-02 / data leaks: the secrets of the employee app never reach the logs.
    [Fact]
    public async Task The_logs_never_hold_the_device_secret_the_tokens_or_the_qr_key()
    {
        using var host = new AuthApiFactory();
        var world = await WorldAsync(host);
        var worker = await AddWorkerAsync(world);
        var linked = await LinkDeviceAsync(host, world, worker);
        var refreshed = await (await RefreshAsync(host.CreateClient(), linked.GetProperty("refreshToken").GetString()!)).JsonAsync(HttpStatusCode.OK);
        var relogin = await (await LoginOnDeviceAsync(host.CreateClient(), worker.Phone, linked)).JsonAsync(HttpStatusCode.OK);
        await host.AsEmployee(refreshed).PostAsync(Logout, null);

        var secrets = new[]
        {
            relogin.GetProperty("deviceSecret").GetString()!,
            relogin.GetProperty("refreshToken").GetString()!,
            linked.GetProperty("deviceSecret").GetString()!,
            linked.GetProperty("refreshToken").GetString()!,
            linked.GetProperty("accessToken").GetString()!,
            refreshed.GetProperty("refreshToken").GetString()!,
            linked.GetProperty("qrKey").GetString()!,
        };
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(host.Logs.Lines, line => line.Contains(secret, StringComparison.Ordinal));
        }
    }

    // Finding L3: the answers that carry a login code, tokens or the device secret are not marked "no-store".
    [Fact]
    public async Task Finding_the_answers_that_carry_a_code_or_tokens_are_not_cacheable()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var issued = await world.Manager.PostAsync($"/api/employees/{worker.Id}/login-codes", null);
        var code = (await issued.JsonAsync(HttpStatusCode.Created)).GetProperty("code").GetString()!;
        var login = await LoginWithCodeAsync(factory.CreateClient(), worker.Phone, code);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        foreach (var response in new[] { issued, login })
        {
            Assert.True(response.Headers.CacheControl?.NoStore == true, $"{response.RequestMessage?.RequestUri}: no Cache-Control: no-store");
        }
    }

    // Finding L5 (PM decision): a typo that cannot be a code (5 or 7 digits, letters) uses up one of the 5 tries.
    [Fact]
    public async Task Finding_a_malformed_code_does_not_use_up_a_try()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var code = await IssueCodeAsync(world.Manager, worker.Id);
        var phone = factory.CreateClient();

        foreach (var typo in new[] { code[..5], code + "0", "abcdef", "12-345", code[1..] })
        {
            Assert.NotEqual(HttpStatusCode.OK, (await LoginWithCodeAsync(phone, worker.Phone, typo)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await LoginWithCodeAsync(phone, worker.Phone, code)).StatusCode);
    }
}
