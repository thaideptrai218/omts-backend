using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Payments;

namespace OMTS.Api.IntegrationTests.ReceivingBankAccounts;

/// <summary>Shared by the DEBT-01 tests. Every account number holds letters: fictional, not anyone's account.</summary>
internal static class ReceivingBankAccountTestKit
{
    public const string Route = "/api/cooperative/receiving-bank-account";
    public const string AuditAction = "Cooperative.ReceivingBankAccountChanged";
    public const string Vietcombank = "970436";
    public const string Bidv = "970418";
    public const string FirstNumber = "TEST00000001";
    public const string SecondNumber = "TEST00000002";
    public const string RefusedByFake = "REJECT000001";
    public const string SilentForFake = "TIMEOUT00001";

    public static object Body(string? rowVersion, string? bankBin = Vietcombank, string? number = FirstNumber, string? holder = "HTX TRUNG NAM", bool confirm = true) =>
        new { bankBin, accountNumber = number, accountHolderName = holder, confirmAccount = confirm, rowVersion };

    public static async Task<string> RowVersionAsync(HttpClient owner) =>
        (await owner.GetFromJsonAsync<JsonElement>(Route)).GetProperty("rowVersion").GetString()!;

    public static async Task<JsonElement> AccountAsync(HttpClient owner) =>
        (await owner.GetFromJsonAsync<JsonElement>(Route)).GetProperty("account");

    public static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
    {
        await response.Content.LoadIntoBufferAsync();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>The cooperative and a logged-in Owner, on <paramref name="api"/> when the test changed the host.</summary>
    public static async Task<HttpClient> OwnerAsync(AuthApiFactory factory, WebApplicationFactory<Program>? api = null)
    {
        await factory.EnsureCooperativeAsync();
        var password = AcsTestKit.NewPassword();
        var account = await factory.AddAccountAsync(AcsTestKit.NewCode(), password);
        var client = (api ?? factory).CreateClient();
        var tokens = await AcsTestKit.LoginTokensAsync(client, account.Username, password);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    public static WebApplicationFactory<Program> WithService(AuthApiFactory factory, IPaymentNotificationService service, int? timeoutSeconds = null) =>
        factory.WithWebHostBuilder(b =>
        {
            if (timeoutSeconds is { } seconds)
            {
                b.UseSetting("Payments:TimeoutSeconds", seconds.ToString());
            }

            b.ConfigureTestServices(services => services.AddSingleton(service));
        });
}
