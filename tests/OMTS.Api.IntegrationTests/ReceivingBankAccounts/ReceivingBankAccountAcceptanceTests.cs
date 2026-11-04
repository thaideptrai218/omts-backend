using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Payments;
using static OMTS.Api.IntegrationTests.ReceivingBankAccounts.ReceivingBankAccountTestKit;

namespace OMTS.Api.IntegrationTests.ReceivingBankAccounts;

/// <summary>DEBT-01.0.E2: a service that refuses, fails or does not answer keeps the old account, and when it is asked.</summary>
public sealed class ReceivingBankAccountAcceptanceTests
{
    [Fact]
    public async Task A_refusal_answers_MSG_DEBT_02_and_keeps_the_old_account_also_when_confirmed()
    {
        using var api = new AuthApiFactory();
        var owner = await OwnerAsync(api);
        await SaveFirstAccountAsync(owner);
        var before = (await AccountAsync(owner)).GetRawText();

        foreach (var confirm in new[] { false, true })
        {
            var refused = await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner), number: RefusedByFake, confirm: confirm));
            var problem = await ProblemAsync(refused);

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("MSG-DEBT-02", problem.GetProperty("code").GetString());
            Assert.Equal(
                "The payment notification service did not accept account REJECT000001 at Vietcombank. The receiving bank account has not been changed. Please check the account, or try again later.",
                problem.GetProperty("detail").GetString());
        }

        Assert.Equal(before, (await AccountAsync(owner)).GetRawText());
        Assert.Equal(1, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == AuditAction)));
    }

    [Fact]
    public async Task No_answer_in_time_answers_503_MSG_DEBT_02_and_keeps_the_old_account()
    {
        using var factory = new AuthApiFactory();
        await using var api = factory.WithWebHostBuilder(b => b.UseSetting("Payments:TimeoutSeconds", "1"));
        var owner = await OwnerAsync(factory, api);
        await SaveFirstAccountAsync(owner);
        var before = (await AccountAsync(owner)).GetRawText();

        var timedOut = await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner), number: SilentForFake));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, timedOut.StatusCode);
        Assert.Equal("MSG-DEBT-02", await timedOut.ErrorCodeAsync());
        Assert.Equal(before, (await AccountAsync(owner)).GetRawText());
    }

    [Fact]
    public async Task A_service_that_ignores_the_cancellation_still_ends_at_the_timeout()
    {
        var never = new TaskCompletionSource<AccountAcceptanceResult>();
        using var factory = new AuthApiFactory();
        await using var api = WithService(factory, new ScriptedPaymentService((_, _) => never.Task), timeoutSeconds: 1);
        var owner = await OwnerAsync(factory, api);

        var response = await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner)));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("MSG-DEBT-02", await response.ErrorCodeAsync());
        Assert.Equal(JsonValueKind.Null, (await AccountAsync(owner)).ValueKind);
    }

    [Fact]
    public async Task A_service_that_throws_answers_503_and_its_text_stays_out_of_the_log()
    {
        var service = new ScriptedPaymentService((account, _) => throw new InvalidOperationException($"bad account {account.AccountNumber}"));
        using var factory = new AuthApiFactory();
        await using var api = WithService(factory, service);
        var owner = await OwnerAsync(factory, api);

        var response = await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner)));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("MSG-DEBT-02", await response.ErrorCodeAsync());
        Assert.Equal(JsonValueKind.Null, (await AccountAsync(owner)).ValueKind);
        Assert.DoesNotContain(FirstNumber, string.Join('\n', factory.Logs.Lines));
    }

    [Fact]
    public async Task The_service_is_asked_before_the_question_and_again_by_the_confirming_request()
    {
        var service = ScriptedPaymentService.Answering(AccountAcceptanceResult.Accepted);
        using var factory = new AuthApiFactory();
        await using var api = WithService(factory, service);
        var owner = await OwnerAsync(factory, api);
        var rowVersion = await RowVersionAsync(owner);

        Assert.Equal("MSG-DEBT-01", await (await owner.PutAsJsonAsync(Route, Body(rowVersion, confirm: false))).ErrorCodeAsync());
        Assert.Equal(1, service.Calls);

        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(Route, Body(rowVersion))).StatusCode);
        Assert.Equal(2, service.Calls);
    }

    [Fact]
    public async Task A_confirmation_sent_without_the_question_still_needs_the_service()
    {
        using var factory = new AuthApiFactory();
        await using var api = WithService(factory, ScriptedPaymentService.Answering(AccountAcceptanceResult.Rejected("NO")));
        var owner = await OwnerAsync(factory, api);

        var response = await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner)));

        Assert.Equal("MSG-DEBT-02", await response.ErrorCodeAsync());
        Assert.Equal(JsonValueKind.Null, (await AccountAsync(owner)).ValueKind);
    }

    [Fact]
    public async Task A_stale_form_does_not_ask_the_service()
    {
        var service = ScriptedPaymentService.Answering(AccountAcceptanceResult.Accepted);
        using var factory = new AuthApiFactory();
        await using var api = WithService(factory, service);
        var owner = await OwnerAsync(factory, api);

        Assert.Equal("MSG06", await (await owner.PutAsJsonAsync(Route, Body("AAAAAAAAB9E="))).ErrorCodeAsync());
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task The_service_gets_the_normalised_account()
    {
        ReceivingAccountToAccept? asked = null;
        var service = new ScriptedPaymentService((account, _) =>
        {
            asked = account;
            return Task.FromResult(AccountAcceptanceResult.Accepted);
        });
        using var factory = new AuthApiFactory();
        await using var api = WithService(factory, service);
        var owner = await OwnerAsync(factory, api);

        await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner), $" {Bidv} ", holder: " htx trung nam "));

        Assert.Equal(new ReceivingAccountToAccept(Bidv, "BIDV", FirstNumber, "HTX TRUNG NAM"), asked);
    }

    private static async Task SaveFirstAccountAsync(HttpClient owner) =>
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner)))).StatusCode);
}
