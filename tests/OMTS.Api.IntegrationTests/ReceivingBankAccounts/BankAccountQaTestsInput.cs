using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Payments;
using static OMTS.Api.IntegrationTests.ReceivingBankAccounts.ReceivingBankAccountTestKit;

namespace OMTS.Api.IntegrationTests.ReceivingBankAccounts;

/// <summary>QA of the checks of BR-DEBT-01: number, holder, bank, malformed bodies. A refusal never reaches the service.</summary>
public sealed class BankAccountQaTestsInput
{
    [Theory]
    [InlineData("TEST\u200B0001")]
    [InlineData("\u200BTEST0001")]
    [InlineData("TEST0001\uFEFF")]
    [InlineData("TEST\u00A00001")]
    [InlineData("TEST\u00AD0001")]
    [InlineData("٠١٢٣٤٥")]
    [InlineData("۰۱۲۳۴۵")]
    [InlineData("१२३४५६")]
    [InlineData("１２３４５６")]
    [InlineData("ＴＥＳＴ０１")]
    [InlineData("TEST0001\u0000")]
    [InlineData("TEST\n0001")]
    [InlineData("TEST-0001")]
    [InlineData("TEST.0001")]
    [InlineData("TEST_0001")]
    [InlineData("TEST+0001")]
    [InlineData("<script>")]
    [InlineData("TEST%0A1")]
    public async Task An_account_number_with_any_other_character_is_refused(string number) =>
        await AssertRefusedAsync(Vietcombank, number, "HTX", "Account number");

    [Fact]
    public async Task A_very_long_account_number_is_refused_and_not_echoed()
    {
        var huge = new string('T', 1_000_000);

        var body = await AssertRefusedAsync(Vietcombank, huge, "HTX", "Account number");

        Assert.DoesNotContain("TTTTTTTT", body);
    }

    [Theory]
    [InlineData("\u00A0TEST0001\u3000")]
    [InlineData("\tTEST0001\r\n")]
    [InlineData(" TEST0001 ")]
    public async Task Whitespace_around_the_number_is_trimmed(string number)
    {
        using var api = new AuthApiFactory();
        var owner = await OwnerAsync(api);

        var saved = await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner), number: number));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("TEST0001", (await AccountAsync(owner)).GetProperty("accountNumber").GetString());
    }

    // The wallets and the finance company of the published list (api.vietqr.io/v2/banks, read on 05/11/2026).
    [Theory]
    [InlineData("971005")]
    [InlineData("971011")]
    [InlineData("971025")]
    [InlineData("971101")]
    [InlineData("971032")]
    [InlineData("977777")]
    [InlineData("999999")]
    [InlineData("９７０４３６")]
    [InlineData("٩٧٠٤٣٦")]
    [InlineData("970436\u200B")]
    [InlineData("970 436")]
    [InlineData("9704360")]
    public async Task A_wallet_or_unknown_or_disguised_bin_is_refused(string bin) =>
        await AssertRefusedAsync(bin, FirstNumber, "HTX", "Bank");

    [Fact]
    public async Task A_bin_sent_as_a_json_number_is_refused_before_the_service()
    {
        var service = ScriptedPaymentService.Answering(AccountAcceptanceResult.Accepted);
        using var factory = new AuthApiFactory();
        await using var api = WithService(factory, service);
        var owner = await OwnerAsync(factory, api);
        var json = $"{{\"bankBin\":970436,\"accountNumber\":\"{FirstNumber}\",\"accountHolderName\":\"HTX\",\"confirmAccount\":true,\"rowVersion\":\"{await RowVersionAsync(owner)}\"}}";

        var response = await owner.PutAsync(Route, new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, service.Calls);
    }

    [Theory]
    [InlineData("\u3000\u00A0")]
    [InlineData("HTX\tTRUNG")]
    [InlineData("HTX\u0000")]
    [InlineData("HTX\u0085TRUNG")]
    [InlineData("HTX\r\nTRUNG")]
    public async Task A_holder_name_blank_or_with_control_characters_is_refused(string holder) =>
        await AssertRefusedAsync(Vietcombank, FirstNumber, holder, "Account holder name");

    // BR-DEBT-01: the holder name is mandatory. These have nothing a person can read, yet each one goes to the service and is saved.
    [Theory]
    [InlineData("\u200B")]
    [InlineData("\u200B\u200C\u200D")]
    [InlineData("\uFEFF")]
    [InlineData("\u2060")]
    [InlineData("\u00AD")]
    [InlineData("\u202E")]
    [InlineData("\u0301")]
    public async Task L1_a_holder_name_with_nothing_to_read_is_refused(string holder) =>
        await AssertRefusedAsync(Vietcombank, FirstNumber, holder, "Account holder name");

    // The client list promises a refusal for a line break, and U+2028 and U+2029 are line breaks.
    [Theory]
    [InlineData("HTX\u2028TRUNG NAM")]
    [InlineData("HTX\u2029TRUNG NAM")]
    public async Task L1_a_holder_name_with_a_unicode_line_break_is_refused(string holder) =>
        await AssertRefusedAsync(Vietcombank, FirstNumber, holder, "Account holder name");

    // A right-to-left override shows the name in the MSG-DEBT-01 question in another order than it is stored and sent.
    [Theory]
    [InlineData("HTX \u202EMAN GNURT")]
    [InlineData("HTX\u200B TRUNG NAM")]
    public async Task L1_a_holder_name_with_invisible_format_characters_is_refused(string holder) =>
        await AssertRefusedAsync(Vietcombank, FirstNumber, holder, "Account holder name");

    [Fact]
    public async Task The_holder_limit_counts_at_the_column_and_a_very_long_name_is_not_echoed()
    {
        using var api = new AuthApiFactory();
        var owner = await OwnerAsync(api);
        var emoji = string.Concat(Enumerable.Repeat("🦪", 100));

        var fits = await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner), holder: emoji));
        var tooLong = await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner), holder: emoji + "A"));
        var huge = await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner), holder: new string('H', 1_000_000)));

        Assert.Equal(HttpStatusCode.OK, fits.StatusCode);
        Assert.Equal("MSG-GEN-01", await tooLong.ErrorCodeAsync());
        Assert.Equal("MSG-GEN-01", await huge.ErrorCodeAsync());
        Assert.DoesNotContain("HHHHHHHH", await huge.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"x\"")]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("{\"accountNumber\":\"\\ud83d\"}")]
    [InlineData("{\"accountHolderName\":\"\\ud83d\"}")]
    [InlineData("{\"accountHolderName\":\"\\udc00A\"}")]
    [InlineData("{\"rowVersion\":{}}")]
    public async Task A_malformed_body_answers_400_and_never_reaches_the_service(string json)
    {
        var service = ScriptedPaymentService.Answering(AccountAcceptanceResult.Accepted);
        using var factory = new AuthApiFactory();
        await using var api = WithService(factory, service);
        var owner = await OwnerAsync(factory, api);

        var response = await owner.PutAsync(Route, new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, service.Calls);
    }

    [Theory]
    [InlineData("AAAA")]
    [InlineData("AAAAAAAAB9E=")]
    public async Task A_forged_version_answers_msg06_before_the_service(string rowVersion)
    {
        var service = ScriptedPaymentService.Answering(AccountAcceptanceResult.Accepted);
        using var factory = new AuthApiFactory();
        await using var api = WithService(factory, service);
        var owner = await OwnerAsync(factory, api);

        var response = await owner.PutAsJsonAsync(Route, Body(rowVersion));

        Assert.Equal("MSG06", await response.ErrorCodeAsync());
        Assert.Equal(0, service.Calls);
    }

    private static async Task<string> AssertRefusedAsync(string? bin, string? number, string? holder, string field)
    {
        var service = ScriptedPaymentService.Answering(AccountAcceptanceResult.Accepted);
        using var factory = new AuthApiFactory();
        await using var api = WithService(factory, service);
        var owner = await OwnerAsync(factory, api);

        var response = await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner), bin, number, holder));
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"HTTP {(int)response.StatusCode}: {body[..Math.Min(300, body.Length)]}");
        var problem = await ProblemAsync(response);
        Assert.Equal("MSG-GEN-01", problem.GetProperty("code").GetString());
        Assert.Equal(field, problem.GetProperty("field").GetString());
        Assert.Equal(0, service.Calls);
        Assert.Equal(JsonValueKind.Null, (await AccountAsync(owner)).ValueKind);
        return body;
    }
}
