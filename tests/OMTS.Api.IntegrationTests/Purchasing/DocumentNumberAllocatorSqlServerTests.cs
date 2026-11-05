using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>The document sequences on SQL Server: parallel callers each get their own number, also on the first use of a key.</summary>
public class DocumentNumberAllocatorSqlServerTests
{
    [SqlServerFact]
    public async Task Parallel_callers_of_a_new_key_get_every_number_once_without_gaps_and_keys_count_apart()
    {
        await using var host = await FarmingHost.StartAsync();
        var key = $"T{Guid.NewGuid():N}"[..12];
        var otherKey = $"U{Guid.NewGuid():N}"[..12];

        async Task<long> NextAsync(string name)
        {
            await using var scope = host.Api.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IDocumentNumberAllocator>().NextAsync(name, CancellationToken.None);
        }

        var numbers = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => NextAsync(key))));

        Assert.Equal(Enumerable.Range(1, 32).Select(n => (long)n), numbers.Order());
        Assert.Equal(1, await NextAsync(otherKey));
        Assert.Equal(33, await NextAsync(key));
    }
}
