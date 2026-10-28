using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>
/// QA of OMTS-216 PR 0.2 on the cross-cutting paths: the exception handler, the permission filter, the account
/// state gate and the per-IP limiter answer with the SRS 27/10 code as <c>title</c> and <c>code</c>, and send
/// <c>legacyCode</c> exactly when the code changed (old codes kept as the code send none).
/// </summary>
public class MessageCodeQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // ProblemExceptionHandler: a duplicate is MSG-GEN-02 (MSG03 before); MSG06, MSG05 and UNEXPECTED_ERROR are kept.
    [Fact]
    public async Task Exception_handler_sends_the_new_code_with_its_legacy_code_and_kept_codes_without_one()
    {
        var client = factory.CreateClient();

        var duplicate = await (await client.GetAsync("/test-only/qa-messages/duplicate")).SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02", "MSG03");
        var concurrency = await (await client.GetAsync("/test-only/qa-messages/concurrency")).SrsProblemAsync(HttpStatusCode.Conflict, "MSG06", null);
        var crash = await (await client.GetAsync("/test-only/qa-messages/crash")).SrsProblemAsync(HttpStatusCode.InternalServerError, "UNEXPECTED_ERROR", null);
        var storage = await (await client.GetAsync("/test-only/storage-read-failure")).SrsProblemAsync(HttpStatusCode.BadGateway, "MSG05", null);

        Assert.Equal("Record already exists. Your entries are kept: change them, or open the existing record.", duplicate.Detail());
        Assert.StartsWith("The data was just updated by another user.", concurrency.Detail());
        Assert.Equal("An unexpected error occurred. Please try again later.", crash.Detail());
        Assert.DoesNotContain("secret", crash.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("The file operation could not be completed. The source data has not changed; please try again.", storage.Detail());
    }

    // RequirePermission: a missing or unreadable zone is MSG-GEN-01 naming the parameter (literal "MSG01" before, without a field).
    [Fact]
    public async Task Permission_filter_without_a_zone_answers_msg_gen_01_and_another_zone_msg_gen_08()
    {
        var zone = await factory.AddZoneAsync(NewCode());
        var other = await factory.AddZoneAsync(NewCode());
        var staff = await factory.StaffAsync(zone, (PermissionFunctions.WaterMeasurements, true, false, false));

        var missing = await (await staff.Client.GetAsync("/test-only/qa-messages/zone-from-query")).SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01");
        var unreadable = await (await staff.Client.GetAsync("/test-only/qa-messages/zone-from-query?zoneId=seven")).SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01");
        var denied = await (await staff.Client.GetAsync($"/test-only/qa-messages/zone-from-query?zoneId={other.Id}")).SrsProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08", "MSG02");
        var allowed = await staff.Client.GetAsync($"/test-only/qa-messages/zone-from-query?zoneId={zone.Id}");

        Assert.Equal("zoneId", missing.Str("field"));
        Assert.Equal("zoneId is missing or invalid. Please check and enter it again.", missing.Detail());
        Assert.Equal("zoneId", unreadable.Str("field"));
        Assert.Equal("You do not have permission to perform this action. Nothing has been changed.", denied.Detail());
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    // AccountStateGateFilter: MSG-ACCOUNT-04 (MSG67 before) with the SRS text; PASSWORD_CHANGE_REQUIRED is technical and kept.
    [Fact]
    public async Task Account_state_gate_answers_msg_account_04_with_legacy_code_and_the_kept_technical_code_without()
    {
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader, a => { a.PhoneNumber = null; a.PhoneVerifiedAt = null; });
        var temporary = await factory.AddAccountAsync(NewCode(), Password, configure: a => { a.MustChangePassword = true; a.TemporaryPasswordIssuedAt = DateTimeOffset.UtcNow; });

        var phone = await (await (await factory.LoginAsync(trader.Username)).GetAsync("/api/me/permissions"))
            .SrsProblemAsync(HttpStatusCode.Forbidden, "MSG-ACCOUNT-04", "MSG67");
        await (await (await factory.LoginAsync(temporary.Username)).GetAsync("/api/me/permissions"))
            .SrsProblemAsync(HttpStatusCode.Forbidden, "PASSWORD_CHANGE_REQUIRED", null);

        Assert.Equal(
            "Your account needs a verified phone number before you can use OMTS. Please enter or confirm your phone number, then enter the verification code sent to it by SMS.",
            phone.Detail());
    }

    // The per-IP limiter writes its own body: the kept TOO_MANY_REQUESTS code, never a legacyCode.
    [Fact]
    public async Task Per_ip_limit_keeps_too_many_requests_without_a_legacy_code()
    {
        using var api = factory.WithWebHostBuilder(b => b.UseSetting("RateLimits:LoginsPerMinute", "1"));
        var client = api.CreateClient();

        await client.PostAsJsonAsync("/api/auth/login", new { identifier = NewCode(), password = Password });
        var refused = await (await client.PostAsJsonAsync("/api/auth/login", new { identifier = NewCode(), password = Password }))
            .SrsProblemAsync(HttpStatusCode.TooManyRequests, "TOO_MANY_REQUESTS", null);

        Assert.Equal("Too many requests from this device. Please wait a minute and try again.", refused.Detail());
    }

    // Login refusals of ACCOUNT-02 not checked with their legacy code elsewhere: Locked (MSG13) and the session (MSG19 kept).
    [Fact]
    public async Task Login_refusals_carry_their_srs_texts_and_legacy_codes()
    {
        var locked = await factory.AddAccountAsync(NewCode(), Password, configure: a => a.StatusId = OMTS.Domain.Constants.StatusIds.Account.Locked);
        var client = factory.CreateClient();

        var unusable = await (await client.PostAsJsonAsync("/api/auth/login", new { identifier = locked.Username, password = Password }))
            .SrsProblemAsync(HttpStatusCode.Forbidden, "MSG-ACCOUNT-07", "MSG13");
        var session = await (await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = "unknown" }))
            .SrsProblemAsync(HttpStatusCode.Unauthorized, "MSG19", null);

        Assert.Equal("This account cannot be used at the moment. Please contact the Cooperative Owner.", unusable.Detail());
        Assert.Equal("Your login session has expired. Please log in again.", session.Detail());
    }
}
