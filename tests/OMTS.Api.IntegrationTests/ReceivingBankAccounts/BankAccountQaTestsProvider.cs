using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Infrastructure.Bootstrap.Demo;
using static OMTS.Api.IntegrationTests.ReceivingBankAccounts.ReceivingBankAccountTestKit;

namespace OMTS.Api.IntegrationTests.ReceivingBankAccounts;

/// <summary>QA of <c>Payments:*</c>: provider choice, startup checks, what the settings show, and the numbers kept in tests and demo data.</summary>
public sealed class BankAccountQaTestsProvider
{
    private const string SecretSetting = "QA_SETTING_NOT_TO_SHOW";

    [Fact]
    public async Task Outside_development_the_host_warns_and_shows_no_setting_of_the_section()
    {
        using var factory = new AuthApiFactory();
        await using var api = factory.WithWebHostBuilder(b => b
            .UseEnvironment("Production")
            .UseSetting("Ocr:Provider", "")
            .UseSetting("Payments:Provider", "Fake")
            .UseSetting("Payments:ApiKey", SecretSetting)
            .UseSetting("Payments:Fake:RejectAccounts:0", SecretSetting));
        var owner = await OwnerAsync(factory, api);

        var refused = await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner)));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.DoesNotContain(SecretSetting, await refused.Content.ReadAsStringAsync());
        var logs = string.Join('\n', factory.Logs.Lines);
        Assert.Contains("Payment notification provider is not configured", logs);
        Assert.DoesNotContain(SecretSetting, logs);
    }

    [Fact]
    public void An_unknown_provider_stops_the_host_and_names_only_the_setting()
    {
        using var factory = new AuthApiFactory();
        using var api = factory.WithWebHostBuilder(b => b.UseSetting("Payments:Provider", "Nobody").UseSetting("Payments:ApiKey", SecretSetting));

        var failure = Assert.ThrowsAny<Exception>(() => api.CreateClient());

        Assert.Contains("Payments:Provider", failure.ToString());
        Assert.DoesNotContain(SecretSetting, failure.ToString());
        Assert.DoesNotContain(SecretSetting, string.Join('\n', factory.Logs.Lines));
    }

    // Adapted with L3: these once counted as 1 second; the lead decided that a value that cannot be meant stops the host.
    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("301")]
    public void A_timeout_outside_its_range_stops_the_host_and_names_the_setting(string seconds)
    {
        using var factory = new AuthApiFactory();
        using var api = factory.WithWebHostBuilder(b => b.UseSetting("Payments:TimeoutSeconds", seconds));

        var failure = Assert.ThrowsAny<Exception>(() => api.CreateClient());

        Assert.Contains("Payments:TimeoutSeconds", failure.ToString());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("300")]
    public async Task A_timeout_at_the_ends_of_its_range_works(string seconds)
    {
        using var factory = new AuthApiFactory();
        await using var api = factory.WithWebHostBuilder(b => b.UseSetting("Payments:TimeoutSeconds", seconds));
        var owner = await OwnerAsync(factory, api);

        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner)))).StatusCode);
    }

    // ExternalServicesStartupCheck: "a configuration error stops the host instead of failing the first user request". The
    // SRS answer for a service that cannot be asked is MSG-DEBT-02, so a host that does start must say that, not 500.
    [Theory]
    [InlineData("soon")]
    [InlineData("5000000")]
    public async Task L3_a_timeout_setting_that_cannot_work_stops_the_host_or_answers_msg_debt_02(string seconds)
    {
        using var factory = new AuthApiFactory();
        await using var api = factory.WithWebHostBuilder(b => b.UseSetting("Payments:TimeoutSeconds", seconds));
        HttpClient owner;
        try
        {
            owner = await OwnerAsync(factory, api);
        }
        catch (Exception exception) when (exception.ToString().Contains("TimeoutSeconds", StringComparison.Ordinal))
        {
            return;
        }

        var response = await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner)));

        Assert.Equal("MSG-DEBT-02", await response.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    // The branch states that its numbers hold letters so that nobody takes them for a real account. Digits only at a real
    // bank could be someone's account. Reported for the lead to decide: where the employee rule allows digits only, the
    // lead chose the fictional pattern 9999 0000 0xxx, which this now accepts (see IsObviouslyNoAccount).
    [Fact]
    public void L5_demo_employees_hold_no_number_that_could_be_a_real_account()
    {
        var plausible = DemoEmployeePlans.All
            .Where(p => p.BankAccountNumber.All(char.IsAsciiDigit) && !IsObviouslyNoAccount(p.BankAccountNumber))
            .Select(p => $"{p.BankName} {p.BankAccountNumber}")
            .ToList();

        Assert.Empty(plausible);
    }

    [Fact]
    public void L5_tests_hold_no_number_that_could_be_a_real_account()
    {
        var root = RepositoryRoot();
        var plausible = new List<string>();
        foreach (var file in new[]
        {
            "tests/OMTS.Api.IntegrationTests/Cooperatives/CooperativeProfileTests.cs",
            "tests/OMTS.Api.IntegrationTests/Employees/EmployeeRequests.cs",
            "tests/OMTS.Api.IntegrationTests/Employees/EmployeeTests.cs",
            "tests/OMTS.Api.IntegrationTests/Employees/EmployeeStatusAndAuditTests.cs",
            "tests/OMTS.Application.UnitTests/Features/ReceivingBankAccounts/VietQrPayloadTests.cs",
        })
        {
            var text = File.ReadAllText(Path.Combine(root, file));
            plausible.AddRange(Regex.Matches(text, "(?:[Aa]ccountNumber = |Bank\\(|\"[0-9]{6}\", )\"([0-9]{8,19})\"").Select(m => $"{Path.GetFileName(file)}: {m.Groups[1].Value}"));
        }

        Assert.Empty(plausible.Where(p => !IsObviouslyNoAccount(p.Split(' ')[^1])).Distinct());
    }

    private static bool IsObviouslyNoAccount(string number) =>
        number.Distinct().Count() == 1 || number.StartsWith("99990000", StringComparison.Ordinal);

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OMTS.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"OMTS.sln not found above {AppContext.BaseDirectory}.");
    }
}
