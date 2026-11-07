using System.Runtime.CompilerServices;

namespace OMTS.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Runs once, before any test host is built. Each host otherwise watches appsettings.json for changes, and each watcher
/// holds an inotify instance on Linux. CI allows 128 per user, and the suite builds more hosts than that, so tests failed
/// with "The configured user limit (128) on the number of inotify instances has been reached". No test reloads settings.
/// </summary>
internal static class TestHostEnvironment
{
    [ModuleInitializer]
    public static void DisableConfigurationReload() =>
        Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");
}
