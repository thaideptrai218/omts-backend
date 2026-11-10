using System.Net;
using System.Net.Http.Json;
using EndpointDataSource = Microsoft.AspNetCore.Routing.EndpointDataSource;
using HttpMethodMetadata = Microsoft.AspNetCore.Routing.HttpMethodMetadata;
using RouteEndpoint = Microsoft.AspNetCore.Routing.RouteEndpoint;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Payments;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.ReceivingBankAccounts.ReceivingBankAccountTestKit;

namespace OMTS.Api.IntegrationTests.ReceivingBankAccounts;

/// <summary>QA of §I.4.4 on every route of DEBT-01, and a sweep of the other routes for the account.</summary>
public sealed class BankAccountQaTestsAccess
{
    private const string SweepNumber = "QASWEEP00077";
    private const string SweepHolder = "QA KIEM THU KHONG LO";

    [Theory]
    [InlineData(StaffRole.ZoneManager, false)]
    [InlineData(StaffRole.ZoneManager, true)]
    [InlineData(StaffRole.Accountant, false)]
    [InlineData(StaffRole.Accountant, true)]
    public async Task Staff_of_one_zone_or_all_zones_are_refused_on_every_route_and_the_service_is_not_asked(StaffRole role, bool allZones)
    {
        var service = ScriptedPaymentService.Answering(AccountAcceptanceResult.Accepted);
        using var factory = new AuthApiFactory();
        await using var api = WithService(factory, service);
        await factory.EnsureCooperativeAsync();
        var password = NewPassword();
        var staff = await factory.AddAccountAsync(NewCode(), password, SubjectType.Staff);
        await factory.GrantAsync(staff, role, allZones ? null : await factory.AddZoneAsync(NewCode()));
        var client = await SignInAsync(api, staff.Username, password);

        await AssertRefusedEverywhereAsync(client);

        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task A_trader_is_refused_on_every_route_and_the_service_is_not_asked()
    {
        var service = ScriptedPaymentService.Answering(AccountAcceptanceResult.Accepted);
        using var factory = new AuthApiFactory();
        await using var api = WithService(factory, service);
        await factory.EnsureCooperativeAsync();
        var password = NewPassword();
        var trader = await factory.AddAccountAsync(NewCode(), password, SubjectType.Trader);

        await AssertRefusedEverywhereAsync(await SignInAsync(api, trader.Username, password));

        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task Without_a_valid_token_every_route_answers_401()
    {
        using var api = new AuthApiFactory();
        await api.EnsureCooperativeAsync();
        var anonymous = api.CreateClient();
        var forged = api.CreateClient();
        forged.DefaultRequestHeaders.Authorization = new("Bearer", "a.b.c");

        foreach (var client in new[] { anonymous, forged })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/banks")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Route)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync(Route, Body("AAAA"))).StatusCode);
        }
    }

    // GFR-02: the check runs at each action, so a token issued before the lock opens nothing.
    [Fact]
    public async Task A_locked_owner_is_refused_with_the_token_issued_before()
    {
        var service = ScriptedPaymentService.Answering(AccountAcceptanceResult.Accepted);
        using var factory = new AuthApiFactory();
        await using var api = WithService(factory, service);
        await factory.EnsureCooperativeAsync();
        var password = NewPassword();
        var account = await factory.AddAccountAsync(NewCode(), password);
        var owner = await SignInAsync(api, account.Username, password);
        var version = await RowVersionAsync(owner);

        await factory.UpdateAsync<Account>(account.Id, a => a.StatusId = StatusIds.Account.Locked);

        foreach (var response in new[] { await owner.GetAsync("/api/banks"), await owner.GetAsync(Route), await owner.PutAsJsonAsync(Route, Body(version)) })
        {
            Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"HTTP {(int)response.StatusCode}");
        }

        Assert.Equal(0, service.Calls);
        Assert.Null(await factory.QueryAsync(db => db.Cooperatives.AsNoTracking().Select(c => c.BankAccount).SingleAsync()));
    }

    // ACCOUNT-02.1: an Owner still on a temporary password, or with a phone not verified, does not work yet.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task An_owner_who_must_finish_the_login_steps_does_not_reach_the_account(bool mustChangePassword, bool phoneUnverified)
    {
        var service = ScriptedPaymentService.Answering(AccountAcceptanceResult.Accepted);
        using var factory = new AuthApiFactory();
        await using var api = WithService(factory, service);
        await factory.EnsureCooperativeAsync();
        var password = NewPassword();
        var account = await factory.AddAccountAsync(NewCode(), password, configure: a =>
        {
            a.MustChangePassword = mustChangePassword;
            a.TemporaryPasswordIssuedAt = mustChangePassword ? DateTimeOffset.UtcNow : null;
            a.PhoneVerifiedAt = phoneUnverified ? null : a.PhoneVerifiedAt;
        });
        var owner = await SignInAsync(api, account.Username, password);

        foreach (var response in new[] { await owner.GetAsync(Route), await owner.PutAsJsonAsync(Route, Body("AAAA")) })
        {
            Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"HTTP {(int)response.StatusCode}");
        }

        Assert.Equal(0, service.Calls);
    }

    // BR-DEBT-01, BR-ORIGIN-04: every GET without a route value, for every kind of caller. Only the Owner's own route holds the account.
    [Fact]
    public async Task No_other_get_route_holds_the_number_or_the_holder_for_anyone()
    {
        using var factory = new AuthApiFactory();
        await factory.EnsureCooperativeAsync();
        var ownerPassword = NewPassword();
        var ownerAccount = await factory.AddAccountAsync(NewCode(), ownerPassword);
        var owner = await SignInAsync(factory, ownerAccount.Username, ownerPassword);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner), number: SweepNumber, holder: SweepHolder))).StatusCode);

        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("GET") == true && e.RoutePattern.Parameters.Count == 0)
            .Select(e => "/" + e.RoutePattern.RawText!.TrimStart('/'))
            .Distinct()
            .ToList();
        Assert.Contains("/api/cooperative/profile", routes);
        Assert.Contains("/api/public/cooperative/logo", routes);
        Assert.Contains(Route, routes);
        Assert.True(routes.Count > 15, $"only {routes.Count} routes");

        var callers = new List<(string Who, HttpClient Client)> { ("owner", owner), ("anonymous", factory.CreateClient()) };
        foreach (var (who, role, allZones) in new[] { ("zone manager", StaffRole.ZoneManager, false), ("all zones manager", StaffRole.ZoneManager, true), ("accountant", StaffRole.Accountant, true) })
        {
            var password = NewPassword();
            var staff = await factory.AddAccountAsync(NewCode(), password, SubjectType.Staff);
            await factory.GrantAsync(staff, role, allZones ? null : await factory.AddZoneAsync(NewCode()));
            callers.Add((who, await SignInAsync(factory, staff.Username, password)));
        }

        var traderPassword = NewPassword();
        callers.Add(("trader", await SignInAsync(factory, (await factory.AddAccountAsync(NewCode(), traderPassword, SubjectType.Trader)).Username, traderPassword)));

        var leaks = new List<string>();
        foreach (var (who, client) in callers)
        {
            // The cash book lists its bank accounts, the receiving one among them, to the Owner and the Accountant who keep it.
            var keepsCashBook = who is "owner" or "accountant";
            foreach (var route in routes.Where(r => (who != "owner" || r != Route) && !(keepsCashBook && r == "/api/cash-accounts")))
            {
                var body = await (await client.GetAsync(route)).Content.ReadAsStringAsync();
                if (body.Contains(SweepNumber, StringComparison.OrdinalIgnoreCase) || body.Contains(SweepHolder, StringComparison.OrdinalIgnoreCase))
                {
                    leaks.Add($"{who} {route}");
                }
            }
        }

        Assert.Empty(leaks);
    }

    [Fact]
    public async Task A_profile_save_and_its_public_preview_hold_no_account()
    {
        using var api = new AuthApiFactory();
        var owner = await OwnerAsync(api);
        await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner), number: SweepNumber, holder: SweepHolder));

        var saved = await owner.PutAsJsonAsync("/api/cooperative/profile", Cooperatives.CooperativeProfileRequests.Valid(await Cooperatives.CooperativeProfileRequests.RowVersionAsync(owner)));
        var body = await saved.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.DoesNotContain(SweepNumber, body);
        Assert.DoesNotContain(SweepHolder, body);
        Assert.Equal(SweepNumber, (await AccountAsync(owner)).GetProperty("accountNumber").GetString());
    }

    private static async Task AssertRefusedEverywhereAsync(HttpClient client)
    {
        var body = Body("AAAA");
        foreach (var response in new[] { await client.GetAsync("/api/banks"), await client.GetAsync(Route), await client.PutAsJsonAsync(Route, body) })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("MSG-GEN-08", await response.ErrorCodeAsync());
        }
    }

    private static async Task<HttpClient> SignInAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> api, string username, string password)
    {
        var client = api.CreateClient();
        var tokens = await LoginTokensAsync(client, username, password);
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }
}
