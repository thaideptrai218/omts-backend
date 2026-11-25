using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.EmployeeApp;
using OMTS.Infrastructure.Messaging.Fakes;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Employees.EmployeesV2QaKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.EmployeeAppKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>BR-LABOR-02: the per-IP limits of the two endpoints that issue or check a code.</summary>
public class EmployeeAppRateLimitTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Logins_over_the_per_minute_limit_get_429()
    {
        using var api = factory.WithWebHostBuilder(b => b.UseSetting("RateLimits:EmployeeAppLoginsPerMinute", "2"));
        var client = api.CreateClient();

        var answers = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => client.PostAsJsonAsync(Login, new { phoneNumber = NewPhone() })));

        Assert.Equal(2, answers.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));
        var refused = Assert.Single(answers, r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.Equal("TOO_MANY_REQUESTS", await refused.ErrorCodeAsync());
        Assert.Equal("60", refused.Headers.RetryAfter?.ToString());
    }

    [Fact]
    public async Task Issuing_codes_over_the_per_minute_limit_gets_429_and_issues_no_second_code()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var password = NewPassword();
        var account = await factory.AddAccountAsync(NewCode(), password);
        using var api = factory.WithWebHostBuilder(b => b.UseSetting("RateLimits:EmployeeLoginCodesPerMinute", "1"));
        var owner = WithBearer(api.CreateClient(), (await LoginTokensAsync(api.CreateClient(), account.Username, password)).GetProperty("accessToken").GetString()!);

        var answers = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => owner.PostAsync($"/api/employees/{worker.Id}/login-codes", null)));

        Assert.Single(answers, r => r.StatusCode == HttpStatusCode.Created);
        var refused = Assert.Single(answers, r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.Equal("TOO_MANY_REQUESTS", await refused.ErrorCodeAsync());
        Assert.Single(await factory.CodesAsync(worker.Id));
    }

    // M1: the budget of issuing codes belongs to the signed-in account, not to the client IP: two issuers behind one address have one each,
    // and the anonymous code endpoints (which share an IP budget) do not use it up.
    [Fact]
    public async Task The_code_budget_is_per_account_and_not_shared_with_the_anonymous_code_endpoints()
    {
        var world = await WorldAsync(factory);
        var crew = new List<Worker>();
        for (var i = 0; i < 3; i++)
        {
            crew.Add(await AddWorkerAsync(world));
        }

        using var api = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("RateLimits:EmployeeLoginCodesPerMinute", "2");
            b.UseSetting("RateLimits:CodeRequestsPerMinute", "1");
        });
        async Task<HttpClient> SignedInAsync(string username, string password) =>
            WithBearer(api.CreateClient(), (await LoginTokensAsync(api.CreateClient(), username, password)).GetProperty("accessToken").GetString()!);
        var ownerPassword = NewPassword();
        var ownerAccount = await factory.AddAccountAsync(NewCode(), ownerPassword);
        var owner = await SignedInAsync(ownerAccount.Username, ownerPassword);
        var otherPassword = NewPassword();
        var otherAccount = await factory.AddAccountAsync(NewCode(), otherPassword);
        var other = await SignedInAsync(otherAccount.Username, otherPassword);

        var first = await owner.PostAsync($"/api/employees/{crew[0].Id}/login-codes", null);
        var second = await owner.PostAsync($"/api/employees/{crew[1].Id}/login-codes", null);
        var third = await owner.PostAsync($"/api/employees/{crew[2].Id}/login-codes", null);
        var otherAccountAnswer = await other.PostAsync($"/api/employees/{crew[2].Id}/login-codes", null);

        // 2 a minute for this account (the anonymous limit of 1 does not apply), the 3rd is refused; the other account has a budget of its own.
        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Created), (first.StatusCode, second.StatusCode));
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal("TOO_MANY_REQUESTS", await third.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Created, otherAccountAnswer.StatusCode);
        // Only the other account's code exists for the third worker.
        Assert.Equal(otherAccount.Id, Assert.Single(await factory.CodesAsync(crew[2].Id)).IssuedByAccountId);
    }

    // L2: the employee app logins and the staff logins each have a per-IP budget of their own.
    [Fact]
    public async Task Staff_logins_and_employee_app_logins_do_not_use_up_each_others_budget()
    {
        using var api = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("RateLimits:LoginsPerMinute", "2");
            b.UseSetting("RateLimits:EmployeeAppLoginsPerMinute", "2");
        });
        var client = api.CreateClient();
        Task<HttpResponseMessage> StaffLoginAsync() => client.PostAsJsonAsync("/api/auth/login", new { identifier = NewCode(), password = NewPassword() });
        Task<HttpResponseMessage> EmployeeLoginAsync() => client.PostAsJsonAsync(Login, new { phoneNumber = NewPhone() });

        for (var i = 0; i < 2; i++)
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await StaffLoginAsync()).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await StaffLoginAsync()).StatusCode);
        // The staff budget is spent; the employee one is whole, and spending it does not touch the staff one.
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await EmployeeLoginAsync()).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await EmployeeLoginAsync()).StatusCode);
    }
}
