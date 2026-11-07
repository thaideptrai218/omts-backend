using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;

namespace OMTS.Api.IntegrationTests.Platform;

public class TestHostFileWatcherTests
{
    // A test host that watches its settings files uses up the 128 inotify instances of the Linux CI (TestHostEnvironment).
    [Fact]
    public void A_test_host_does_not_watch_its_settings_files()
    {
        using var factory = new WebApplicationFactory<Program>();

        var configuration = (IConfigurationRoot)factory.Services.GetRequiredService<IConfiguration>();

        var jsonFiles = configuration.Providers.OfType<JsonConfigurationProvider>().ToList();
        Assert.NotEmpty(jsonFiles);
        Assert.All(jsonFiles, provider => Assert.False(provider.Source.ReloadOnChange, provider.Source.Path));
    }
}
