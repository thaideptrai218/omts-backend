using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Employees;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Accounts.AccountRulesQaTestsKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Accounts;

/// <summary>
/// Independent QA of the account rules (OMTS-216): the phone gate, the forced change, staff without a pair and a Locked account.
/// A test whose comment starts with QA-n fails on purpose: it shows finding n of reports/qa-261104-account-rules.md.
/// </summary>
public partial class AccountRulesQaTestsForcedChange(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly string Pw = NewPassword();

    private static readonly string[] OpenInThePhoneStep =
    [
        "GET api/me", "GET api/me/profile", "POST api/auth/logout", "POST api/me/phone-verification", "POST api/me/phone-verification/confirm",
    ];

    private static readonly string[] OpenInTheSecondStep =
    [
        "GET api/me", "POST api/auth/logout", "POST api/me/password", "POST api/me/password/code", "POST api/me/phone-verification", "POST api/me/phone-verification/confirm",
    ];

    // Fine: every endpoint that needs a login answers 403 in the phone step, for an Owner, a staff member and a trader.
    [Theory]
    [InlineData(SubjectType.CooperativeOwner, true)]
    [InlineData(SubjectType.CooperativeOwner, false)]
    [InlineData(SubjectType.Staff, true)]
    [InlineData(SubjectType.Trader, false)]
    public async Task In_the_phone_step_every_endpoint_answers_msg_account_04(SubjectType subjectType, bool temporaryPassword)
    {
        var client = await LoginAsync(subjectType, temporaryPassword, phoneVerified: false);

        Assert.Empty(await SweepAsync(client, OpenInThePhoneStep, "MSG-ACCOUNT-04"));
    }

    // Fine: every other endpoint answers 403 in the password step.
    [Theory]
    [InlineData(SubjectType.CooperativeOwner)]
    [InlineData(SubjectType.Staff)]
    public async Task In_the_password_step_every_other_endpoint_answers_password_change_required(SubjectType subjectType)
    {
        var client = await LoginAsync(subjectType, temporaryPassword: true, phoneVerified: true);

        Assert.Empty(await SweepAsync(client, OpenInTheSecondStep, "PASSWORD_CHANGE_REQUIRED"));
    }

    // ACCOUNT-04 POST-1, ACCOUNT-04.0.E2: the temporary password no longer works after the forced change, so it cannot be the new
    // one: MSG-GEN-01 naming the field, and the forced change stays open.
    [Fact]
    public async Task The_forced_change_refuses_the_temporary_password_as_the_new_one()
    {
        var staff = await AddAsync(SubjectType.Staff, temporaryPassword: true, phoneVerified: true);
        var client = await factory.LoginAsync(staff.Username, Pw);

        var changed = await client.PostAsJsonAsync("/api/me/password", new { newPassword = Pw, passwordConfirmation = Pw });

        Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);
        Assert.Equal("MSG-GEN-01", await changed.ErrorCodeAsync());
        Assert.Equal("Password", (await changed.BodyAsync()).GetProperty("field").GetString());
        Assert.True((await factory.QueryAsync(db => db.Accounts.AsNoTracking().SingleAsync(a => a.Id == staff.Id))).MustChangePassword);
    }

    // BR-ACCOUNT-06: an expired temporary password can no longer be used, and that holds for the session it opened too.
    [Fact]
    public async Task A_session_of_an_expired_temporary_password_cannot_set_a_new_password()
    {
        var staff = await AddAsync(SubjectType.Staff, temporaryPassword: true, phoneVerified: true);
        var client = await factory.LoginAsync(staff.Username, Pw);
        await factory.UpdateAsync<Account>(staff.Id, a => a.TemporaryPasswordIssuedAt = DateTimeOffset.UtcNow.AddHours(-25));
        var newPassword = NewPassword();

        var changed = await client.PostAsJsonAsync("/api/me/password", new { newPassword, passwordConfirmation = newPassword });

        Assert.Equal(HttpStatusCode.Forbidden, changed.StatusCode);
        Assert.Equal("MSG-ACCOUNT-08", await changed.ErrorCodeAsync());
        Assert.Equal(staff.PasswordHash, (await factory.QueryAsync(db => db.Accounts.AsNoTracking().SingleAsync(a => a.Id == staff.Id))).PasswordHash);
    }

    // QA-L6 (ACCOUNT-02.1 step 2: the number entered by the Owner is shown partly hidden; no other function is offered): in the
    // phone step GET api/me/profile gives the whole number, the email and the zone-role pairs to whoever has the temporary password.
    [Fact]
    public async Task The_profile_in_the_phone_step_shows_the_number_partly_hidden()
    {
        var staff = await AddAsync(SubjectType.Staff, temporaryPassword: true, phoneVerified: false);
        var client = await factory.LoginAsync(staff.Username, Pw);

        var profile = await client.GetFromJsonAsync<JsonElement>("/api/me/profile");

        Assert.NotEqual(staff.PhoneNumber, profile.GetProperty("phoneNumber").GetString());
    }

    // LABOR-01, MSG-ACCOUNT-23: the temporary password of an employee account comes with the code the client fills in, also
    // when the account is added later; a save without an account has neither.
    [Fact]
    public async Task An_employee_account_answers_msg_account_23_with_its_temporary_password()
    {
        await factory.EnsureCooperativeAsync();
        var owner = await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Pw)).Username, Pw);
        var zone = await factory.AddZoneAsync(NewCode());

        var saved = await owner.PostAsJsonAsync("/api/employees", EmployeeRequests.Profile(
            $"Nhân viên {NewCode()}", NewPhone(), zone.Id, body => body["account"] = new { username = $"nv{NewCode()}" }));
        var noAccount = await owner.PostAsJsonAsync("/api/employees", EmployeeRequests.Profile($"Nhân viên {NewCode()}", NewPhone(), zone.Id));
        var noAccountBody = await noAccount.BodyAsync();
        var later = await owner.PostAsJsonAsync($"/api/employees/{noAccountBody.GetProperty("employee").GetProperty("id").GetGuid()}/account", new { username = $"nv{NewCode()}" });

        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        Assert.Equal("MSG-ACCOUNT-23", (await saved.BodyAsync()).GetProperty("accountMessageCode").GetString());
        Assert.Equal("MSG-ACCOUNT-23", (await later.BodyAsync()).GetProperty("accountMessageCode").GetString());
        Assert.False(noAccountBody.TryGetProperty("accountMessageCode", out var none) && none.ValueKind != JsonValueKind.Null);
    }

    // Accepted risk, PM decision 03/11, in the report as a question: not a failure. An Owner's first password that leaks, even a month
    // later, is enough for the whole account: the thief puts in his own number, then sets a password with no proof.
    [Fact]
    public async Task A_leaked_first_password_of_the_owner_gives_the_whole_account_but_leaves_an_audit_trail()
    {
        var owner = await AddAsync(SubjectType.CooperativeOwner, temporaryPassword: true, phoneVerified: false);
        await factory.UpdateAsync<Account>(owner.Id, a => a.TemporaryPasswordIssuedAt = DateTimeOffset.UtcNow.AddDays(-30));
        var thief = await factory.LoginAsync(owner.Username, Pw);
        var thiefPhone = NewPhone();
        var thiefPassword = NewPassword();

        await factory.VerifyPhoneAsync(thief, thiefPhone);
        var changed = await thief.PostAsJsonAsync("/api/me/password", new { newPassword = thiefPassword, passwordConfirmation = thiefPassword });

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = owner.Username, password = Pw })).StatusCode);
        var audited = await factory.QueryAsync(db => db.AuditEvents.AsNoTracking().Where(e => e.RecordId == owner.Id).Select(e => e.Action).ToListAsync());
        Assert.Contains("Account.PhoneVerified", audited);
        Assert.Contains("Account.PasswordChanged", audited);
    }

    // Fine: a Locked account: its session ends, its temporary password answers MSG-ACCOUNT-07, a token got before the lock sets nothing.
    [Fact]
    public async Task A_locked_account_can_do_nothing_with_a_session_a_temporary_password_or_a_recovery_token()
    {
        var owner = await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Pw)).Username, Pw);
        var staff = await AddAsync(SubjectType.Staff, temporaryPassword: true, phoneVerified: true);
        var session = await factory.LoginAsync(staff.Username, Pw);
        var anonymous = factory.CreateClient();
        var requestId = (await (await anonymous.PostAsJsonAsync("/api/auth/recovery", new { identifier = staff.PhoneNumber })).BodyAsync()).GetProperty("requestId").GetGuid();
        var token = await VerifyAsync(anonymous, requestId, factory.LastSmsCode(staff.PhoneNumber!));
        var newPassword = NewPassword();

        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/lock", new { reason = "QA" })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await session.PostAsJsonAsync("/api/me/password", new { newPassword, passwordConfirmation = newPassword })).StatusCode);
        Assert.Equal("MSG-ACCOUNT-07", await (await anonymous.PostAsJsonAsync("/api/auth/login", new { identifier = staff.Username, password = Pw })).ErrorCodeAsync());
        Assert.Equal("MSG-ACCOUNT-16", await (await anonymous.PostAsJsonAsync("/api/auth/recovery/reset", new { verificationToken = token, newPassword, passwordConfirmation = newPassword })).ErrorCodeAsync());
        Assert.Equal(staff.PasswordHash, (await factory.QueryAsync(db => db.Accounts.AsNoTracking().SingleAsync(a => a.Id == staff.Id))).PasswordHash);
    }

    // Fine: without a pair a wrong password still answers MSG-ACCOUNT-05, a right one MSG-ACCOUNT-07, and a refresh after the last pair is revoked too.
    [Fact]
    public async Task Staff_without_a_pair_answer_msg_account_07_only_to_the_right_password()
    {
        var owner = await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Pw)).Username, Pw);
        var noPair = await factory.AddAccountAsync(NewCode(), Pw, SubjectType.Staff);
        var withPair = await factory.AddAccountAsync(NewCode(), Pw, SubjectType.Staff);
        var grant = await factory.GrantAsync(withPair, StaffRole.Accountant, null);
        var anonymous = factory.CreateClient();
        var tokens = await LoginTokensAsync(anonymous, withPair.Username, Pw);

        Assert.Equal("MSG-ACCOUNT-05", await (await anonymous.PostAsJsonAsync("/api/auth/login", new { identifier = noPair.Username, password = NewPassword() })).ErrorCodeAsync());
        Assert.Equal("MSG-ACCOUNT-07", await (await anonymous.PostAsJsonAsync("/api/auth/login", new { identifier = noPair.Username, password = Pw })).ErrorCodeAsync());

        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/accounts/{withPair.Id}/grants/{grant.Id}/revoke", new { reason = "QA" })).StatusCode);
        var refreshed = await anonymous.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.GetProperty("refreshToken").GetString() });
        Assert.Equal("MSG-ACCOUNT-07", await refreshed.ErrorCodeAsync());
    }

    private async Task<Account> AddAsync(SubjectType subjectType, bool temporaryPassword, bool phoneVerified)
    {
        var account = await factory.AddAccountAsync(NewCode(), Pw, subjectType, a =>
        {
            a.MustChangePassword = temporaryPassword;
            a.TemporaryPasswordIssuedAt = temporaryPassword ? DateTimeOffset.UtcNow : null;
            a.PhoneVerifiedAt = phoneVerified ? DateTimeOffset.UtcNow : null;
        });
        if (subjectType == SubjectType.Staff)
        {
            await factory.GrantAsync(account, StaffRole.Accountant, null);
        }

        return account;
    }

    private async Task<HttpClient> LoginAsync(SubjectType subjectType, bool temporaryPassword, bool phoneVerified) =>
        await factory.LoginAsync((await AddAsync(subjectType, temporaryPassword, phoneVerified)).Username, Pw);

    /// <summary>Calls every action of the API that needs a login; returns those that do not answer the gate's 403.</summary>
    private async Task<List<string>> SweepAsync(HttpClient client, string[] open, string expectedCode)
    {
        var actions = factory.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items;
        var wrong = new List<string>();
        var called = 0;
        foreach (var action in actions)
        {
            var template = action.AttributeRouteInfo?.Template;
            var methods = action.ActionConstraints?.OfType<HttpMethodActionConstraint>().SelectMany(c => c.HttpMethods).ToList();
            // The test-only controller is not part of the API: it opens its own actions to test the gate.
            if (template is null || methods is null || template.StartsWith("test-only/", StringComparison.Ordinal) || action.EndpointMetadata.OfType<IAllowAnonymous>().Any())
            {
                continue;
            }

            foreach (var method in methods)
            {
                if (open.Contains($"{method} {template}"))
                {
                    continue;
                }

                called++;
                var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/" + Url(template)));
                var code = response.Content.Headers.ContentType?.MediaType?.Contains("json") == true ? await response.ErrorCodeAsync() : null;
                if (response.StatusCode != HttpStatusCode.Forbidden || code != expectedCode)
                {
                    wrong.Add($"{method} {template}: {(int)response.StatusCode} {code}");
                }
            }
        }

        Assert.True(called > 100, $"Only {called} endpoints were called.");
        return wrong;
    }

    private static string Url(string template) => RouteParameter().Replace(template, match => match.Value.Contains(":int") || match.Value.Contains(":long") ? "1" : Guid.NewGuid().ToString());

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex RouteParameter();
}
