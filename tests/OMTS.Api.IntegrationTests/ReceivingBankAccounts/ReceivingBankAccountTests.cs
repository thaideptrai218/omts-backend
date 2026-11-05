using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using static OMTS.Api.IntegrationTests.ReceivingBankAccounts.ReceivingBankAccountTestKit;

namespace OMTS.Api.IntegrationTests.ReceivingBankAccounts;

/// <summary>DEBT-01: the flow of the SRS, the question with its count of old codes, and the audit row.</summary>
public sealed class ReceivingBankAccountTests
{
    [Fact]
    public async Task An_account_is_accepted_then_confirmed_then_saved_with_the_time_and_an_audit_row()
    {
        using var api = new AuthApiFactory();
        var cooperative = await api.EnsureCooperativeAsync();
        var owner = await OwnerAsync(api);

        Assert.Equal(JsonValueKind.Null, (await AccountAsync(owner)).ValueKind);

        var rowVersion = await RowVersionAsync(owner);
        var asked = await owner.PutAsJsonAsync(Route, Body(rowVersion, confirm: false));
        var question = await ProblemAsync(asked);

        Assert.Equal(HttpStatusCode.Conflict, asked.StatusCode);
        Assert.Equal("MSG-DEBT-01", question.GetProperty("code").GetString());
        Assert.Equal("Use account TEST00000001 at Vietcombank (HTX TRUNG NAM) for the VietQR codes of the sales invoices?", question.GetProperty("detail").GetString());
        Assert.Equal("confirmAccount", question.GetProperty("confirmFlag").GetString());
        Assert.Equal(FirstNumber, question.GetProperty("accountNumber").GetString());
        Assert.Equal("Vietcombank", question.GetProperty("bankName").GetString());
        Assert.Equal("HTX TRUNG NAM", question.GetProperty("accountHolder").GetString());
        Assert.Equal(JsonValueKind.Null, question.GetProperty("oldCodesCount").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await AccountAsync(owner)).ValueKind);

        var start = DateTimeOffset.UtcNow;
        var saved = await owner.PutAsJsonAsync(Route, Body(rowVersion));
        var body = await saved.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("MSG-GEN-05", body.GetProperty("messageCode").GetString());
        Assert.Equal("Receiving bank account saved.", body.GetProperty("message").GetString());
        var account = body.GetProperty("receivingBankAccount").GetProperty("account");
        Assert.Equal(Vietcombank, account.GetProperty("bankBin").GetString());
        Assert.Equal("Vietcombank", account.GetProperty("bankName").GetString());
        Assert.Equal(FirstNumber, account.GetProperty("accountNumber").GetString());
        Assert.Equal("HTX TRUNG NAM", account.GetProperty("accountHolderName").GetString());
        Assert.InRange(account.GetProperty("acceptedAt").GetDateTimeOffset(), start.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(account.GetRawText(), (await AccountAsync(owner)).GetRawText());

        var audit = await api.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == AuditAction));
        Assert.Equal(cooperative.Id, audit.RecordId);
        Assert.Null(audit.Before);
        Assert.Contains("\"AccountNumber\":\"********0001\"", audit.After);
        Assert.Contains("\"BankBin\":\"970436\"", audit.After);
        Assert.Contains("\"AcceptedAt\"", audit.After);
        Assert.DoesNotContain(FirstNumber, audit.After);
    }

    [Fact]
    public async Task A_change_keeps_the_values_before_and_after_masked_and_says_how_many_old_codes_there_are()
    {
        using var factory = new AuthApiFactory();
        await using var api = WithCodeCount(factory, 3);
        var owner = await OwnerAsync(factory, api);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner)))).StatusCode);

        var rowVersion = await RowVersionAsync(owner);
        var question = await ProblemAsync(await owner.PutAsJsonAsync(Route, Body(rowVersion, Bidv, SecondNumber, confirm: false)));

        Assert.Equal("MSG-DEBT-01", question.GetProperty("code").GetString());
        Assert.Equal(3, question.GetProperty("oldCodesCount").GetInt32());
        Assert.Equal(
            "3 unpaid invoices have VietQR codes already sent for the old account. A transfer made with them is not recorded automatically. "
            + "Use account TEST00000002 at BIDV (HTX TRUNG NAM) for the VietQR codes of the sales invoices?",
            question.GetProperty("detail").GetString());
        Assert.Equal(FirstNumber, (await AccountAsync(owner)).GetProperty("accountNumber").GetString());

        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(Route, Body(rowVersion, Bidv, SecondNumber))).StatusCode);

        Assert.Equal(SecondNumber, (await AccountAsync(owner)).GetProperty("accountNumber").GetString());
        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == AuditAction && e.Before != null));
        Assert.Contains("\"AccountNumber\":\"********0001\"", audit.Before);
        Assert.Contains("\"BankName\":\"Vietcombank\"", audit.Before);
        Assert.Contains("\"AccountNumber\":\"********0002\"", audit.After);
        Assert.Contains("\"BankName\":\"BIDV\"", audit.After);
    }

    [Theory]
    [InlineData(0, "0 unpaid invoices have VietQR codes")]
    [InlineData(1, "1 unpaid invoice has a VietQR code")]
    public async Task The_old_codes_are_counted_also_when_there_are_none_and_read_right_for_one(int count, string text)
    {
        using var factory = new AuthApiFactory();
        await using var api = WithCodeCount(factory, count);
        var owner = await OwnerAsync(factory, api);
        await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner)));

        var question = await ProblemAsync(await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner), number: SecondNumber, confirm: false)));

        Assert.StartsWith(text, question.GetProperty("detail").GetString());
        Assert.Equal(count, question.GetProperty("oldCodesCount").GetInt32());
    }

    [Fact]
    public async Task A_corrected_holder_name_on_the_same_account_says_nothing_about_old_codes()
    {
        using var factory = new AuthApiFactory();
        await using var api = WithCodeCount(factory, 5);
        var owner = await OwnerAsync(factory, api);
        await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner)));

        var question = await ProblemAsync(await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner), holder: "HTX HAU TRUNG NAM", confirm: false)));

        Assert.StartsWith("Use account", question.GetProperty("detail").GetString());
        Assert.Equal(JsonValueKind.Null, question.GetProperty("oldCodesCount").ValueKind);
    }

    [Fact]
    public async Task A_stale_version_answers_a_conflict_and_changes_nothing()
    {
        using var api = new AuthApiFactory();
        var owner = await OwnerAsync(api);
        await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner)));

        var stale = await owner.PutAsJsonAsync(Route, Body("AAAAAAAAB9E=", number: SecondNumber));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("MSG06", await stale.ErrorCodeAsync());
        Assert.True((await ProblemAsync(stale)).TryGetProperty("currentRowVersion", out _));
        Assert.Equal(FirstNumber, (await AccountAsync(owner)).GetProperty("accountNumber").GetString());
    }

    private static WebApplicationFactory<Program> WithCodeCount(AuthApiFactory factory, int count) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(services => services.AddSingleton<ISentInvoiceQrCodeQuery>(new FixedCodeCount(count))));

    private sealed class FixedCodeCount(int count) : ISentInvoiceQrCodeQuery
    {
        public Task<int> CountUnpaidInvoicesWithSentCodesAsync(CancellationToken cancellationToken) => Task.FromResult(count);
    }
}
