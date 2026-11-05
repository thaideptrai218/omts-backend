using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.ReceivingBankAccounts.ReceivingBankAccountTestKit;

namespace OMTS.Api.IntegrationTests.ReceivingBankAccounts;

/// <summary>
/// <c>Payments:Provider</c>: no provider is chosen, so a deployment accepts no account and only Development has the fake.
/// Also what the adapters write to the log.
/// </summary>
public sealed class PaymentProviderConfigurationTests
{
    [Fact]
    public async Task No_provider_accepts_nothing()
    {
        using var factory = new AuthApiFactory();
        await using var api = factory.WithWebHostBuilder(b => b.UseSetting("Payments:Provider", "None"));
        var owner = await OwnerAsync(factory, api);

        var response = await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner)));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("MSG-DEBT-02", await response.ErrorCodeAsync());
        Assert.Equal(JsonValueKind.Null, (await AccountAsync(owner)).ValueKind);
    }

    [Fact]
    public async Task The_fake_accepts_nothing_outside_development()
    {
        using var factory = new AuthApiFactory();
        await using var api = factory.WithWebHostBuilder(b => b
            .UseEnvironment("Production").UseSetting("Ocr:Provider", "").UseSetting("Payments:Provider", "Fake"));
        var owner = await OwnerAsync(factory, api);

        var response = await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner)));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("MSG-DEBT-02", await response.ErrorCodeAsync());
    }

    [Fact]
    public void An_unknown_provider_stops_the_host()
    {
        using var factory = new AuthApiFactory();
        using var api = factory.WithWebHostBuilder(b => b.UseSetting("Payments:Provider", "Nobody"));

        var failure = Assert.ThrowsAny<Exception>(() => api.CreateClient());

        Assert.Contains("Payments:Provider", failure.Message);
    }

    [Fact]
    public void The_settings_choose_none_and_only_development_the_fake()
    {
        var api = Path.Combine(RepositoryRoot(), "src", "OMTS.Api");
        IConfiguration Load(params string[] files) => files
            .Aggregate(new ConfigurationBuilder().SetBasePath(api), (builder, file) => builder.AddJsonFile(file))
            .Build();

        Assert.Equal("None", Load("appsettings.json")["Payments:Provider"]);
        Assert.Equal("15", Load("appsettings.json")["Payments:TimeoutSeconds"]);
        Assert.Equal("Fake", Load("appsettings.json", "appsettings.Development.json")["Payments:Provider"]);
    }

    [Fact]
    public async Task Logs_never_hold_a_full_account_number()
    {
        using var factory = new AuthApiFactory();
        await using var api = factory.WithWebHostBuilder(b => b.UseSetting("Payments:TimeoutSeconds", "1"));
        var owner = await OwnerAsync(factory, api);

        await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner)));
        await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner), number: RefusedByFake));
        await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner), number: SilentForFake));

        var logs = string.Join('\n', factory.Logs.Lines);
        Assert.Contains("********0001", logs);
        Assert.DoesNotContain(FirstNumber, logs);
        Assert.DoesNotContain(RefusedByFake, logs);
        Assert.DoesNotContain(SilentForFake, logs);
    }

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
