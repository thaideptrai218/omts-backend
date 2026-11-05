using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Payments;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.ReceivingBankAccounts.ReceivingBankAccountTestKit;

namespace OMTS.Api.IntegrationTests.ReceivingBankAccounts;

/// <summary>DEBT-01.0.E1 and the §I.4.4 matrix: rejected input, the list of banks, and who may open any of it.</summary>
public sealed class ReceivingBankAccountInputAndAccessTests
{
    [Theory]
    [InlineData(null, FirstNumber, "HTX", new[] { "Bank" })]
    [InlineData("", FirstNumber, "HTX", new[] { "Bank" })]
    [InlineData("000000", FirstNumber, "HTX", new[] { "Bank" })]
    [InlineData("97043", FirstNumber, "HTX", new[] { "Bank" })]
    [InlineData(Vietcombank, null, "HTX", new[] { "Account number" })]
    [InlineData(Vietcombank, "   ", "HTX", new[] { "Account number" })]
    [InlineData(Vietcombank, "TEST 0001", "HTX", new[] { "Account number" })]
    [InlineData(Vietcombank, "TEST-0001", "HTX", new[] { "Account number" })]
    [InlineData(Vietcombank, "TÀI0001", "HTX", new[] { "Account number" })]
    [InlineData(Vietcombank, "12345678901234567890", "HTX", new[] { "Account number" })]
    [InlineData(Vietcombank, FirstNumber, null, new[] { "Account holder name" })]
    [InlineData(Vietcombank, FirstNumber, "   ", new[] { "Account holder name" })]
    [InlineData(Vietcombank, FirstNumber, "HTX\nTRUNG NAM", new[] { "Account holder name" })]
    [InlineData(null, null, null, new[] { "Bank", "Account number", "Account holder name" })]
    public async Task Invalid_data_names_the_fields_and_does_not_ask_the_service(string? bin, string? number, string? holder, string[] fields)
    {
        var service = ScriptedPaymentService.Answering(AccountAcceptanceResult.Accepted);
        using var factory = new AuthApiFactory();
        await using var api = WithService(factory, service);
        var owner = await OwnerAsync(factory, api);

        var response = await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner), bin, number, holder));
        var problem = await ProblemAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG-GEN-01", problem.GetProperty("code").GetString());
        Assert.Equal(fields[0], problem.GetProperty("field").GetString());
        Assert.Equal(fields, problem.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(0, service.Calls);
        Assert.Equal(JsonValueKind.Null, (await AccountAsync(owner)).ValueKind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not base64!")]
    public async Task A_missing_or_unreadable_version_is_invalid_input(string? rowVersion)
    {
        using var api = new AuthApiFactory();
        var owner = await OwnerAsync(api);

        var response = await owner.PutAsJsonAsync(Route, Body(rowVersion));

        Assert.Equal("MSG-GEN-01", await response.ErrorCodeAsync());
        Assert.Equal("RowVersion", (await ProblemAsync(response)).GetProperty("field").GetString());
    }

    [Fact]
    public async Task Values_are_trimmed_and_the_longest_vietnamese_holder_name_is_kept_in_capitals()
    {
        using var api = new AuthApiFactory();
        var owner = await OwnerAsync(api);
        var holder = new string('ắ', 200);

        var saved = await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner), $" {Vietcombank} ", $" {FirstNumber} ", $"  {holder}  "));
        var tooLong = await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner), holder: holder + "a"));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var account = await AccountAsync(owner);
        Assert.Equal(FirstNumber, account.GetProperty("accountNumber").GetString());
        Assert.Equal(holder.ToUpperInvariant(), account.GetProperty("accountHolderName").GetString());
        Assert.Equal("MSG-GEN-01", await tooLong.ErrorCodeAsync());
    }

    [Fact]
    public async Task The_list_of_banks_gives_the_bin_and_both_names()
    {
        using var api = new AuthApiFactory();
        var owner = await OwnerAsync(api);

        var banks = await owner.GetFromJsonAsync<JsonElement>("/api/banks");

        var bidv = banks.EnumerateArray().Single(b => b.GetProperty("bin").GetString() == Bidv);
        Assert.Equal("BIDV", bidv.GetProperty("shortName").GetString());
        Assert.Equal("Ngân hàng TMCP Đầu tư và Phát triển Việt Nam", bidv.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData(StaffRole.ZoneManager)]
    [InlineData(StaffRole.Accountant)]
    public async Task Only_the_owner_has_access_also_not_staff_of_all_zones(StaffRole role)
    {
        using var api = new AuthApiFactory();
        await api.EnsureCooperativeAsync();
        var password = NewPassword();
        var staff = await api.AddAccountAsync(NewCode(), password, SubjectType.Staff);
        await api.GrantAsync(staff, role, null);
        var client = await api.LoginAsync(staff.Username, password);

        Assert.Equal("MSG-GEN-08", await (await client.GetAsync("/api/banks")).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await client.GetAsync(Route)).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await client.PutAsJsonAsync(Route, Body(""))).ErrorCodeAsync());
        Assert.Null(await api.QueryAsync(db => db.Cooperatives.AsNoTracking().Select(c => c.BankAccount).SingleAsync()));
    }

    [Fact]
    public async Task A_trader_and_an_anonymous_caller_have_no_access()
    {
        using var api = new AuthApiFactory();
        await api.EnsureCooperativeAsync();
        var password = NewPassword();
        var trader = await api.LoginAsync((await api.AddAccountAsync(NewCode(), password, SubjectType.Trader)).Username, password);
        var anonymous = api.CreateClient();

        Assert.Equal("MSG-GEN-08", await (await trader.GetAsync(Route)).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await trader.PutAsJsonAsync(Route, Body(""))).ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/banks")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Route)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(Route, Body(""))).StatusCode);
    }

    [Fact]
    public async Task The_account_never_shows_in_the_cooperative_profile_or_its_public_preview()
    {
        using var api = new AuthApiFactory();
        var owner = await OwnerAsync(api);
        await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner)));

        var profile = await (await owner.GetAsync("/api/cooperative/profile")).Content.ReadAsStringAsync();

        Assert.DoesNotContain(FirstNumber, profile);
        Assert.DoesNotContain("bank", profile, StringComparison.OrdinalIgnoreCase);
    }
}
