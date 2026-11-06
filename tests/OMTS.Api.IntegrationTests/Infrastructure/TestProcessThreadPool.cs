using System.Runtime.CompilerServices;

namespace OMTS.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Many hosts and SQL Server sessions run in this one process. On a CI runner with few cores the thread pool starts with few
/// threads and adds them slowly, and queued work has waited over a minute, failing tests that measure time. A higher minimum
/// starts the threads at once.
/// </summary>
internal static class TestProcessThreadPool
{
    private const int MinimumThreads = 100;

    [ModuleInitializer]
    internal static void RaiseTheMinimum() => ThreadPool.SetMinThreads(MinimumThreads, MinimumThreads);
}
