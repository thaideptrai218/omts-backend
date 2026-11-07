using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Set-up for the FARM-01 / LAB-01 tests on a real SQL Server: migrated database, the deployment's
/// cooperative and a logged-in Cooperative Owner.
/// </summary>
public static class FarmLabSqlServerKit
{
    public static async Task<SqlServerApiFactory> StartAsync(SqlServerTestDatabase database)
    {
        await using (var db = database.CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        return new SqlServerApiFactory(database);
    }

    /// <summary>The cooperative created at deployment (bootstrap) and a client of a logged-in Owner.</summary>
    public static async Task<(HttpClient Owner, Account Account, Cooperative Cooperative)> OwnerWithCooperativeAsync(SqlServerApiFactory api)
    {
        var cooperative = new Cooperative { FullName = "HTX Trung Nam" };
        await ChangeAsync(api, db => db.Cooperatives.Add(cooperative));
        var account = await api.AddAccountAsync($"owner{AcsTestKit.NewCode()}", AcsTestKit.Password);
        var client = api.CreateClient();
        var tokens = await AcsTestKit.LoginTokensAsync(client, account.Username);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return (client, account, cooperative);
    }

    public static async Task<FarmingZone> AddZoneAsync(SqlServerApiFactory api, Guid cooperativeId)
    {
        var code = AcsTestKit.NewCode();
        var zone = new FarmingZone { CooperativeId = cooperativeId, Code = code, Name = $"Zone {code}" };
        await ChangeAsync(api, db => db.FarmingZones.Add(zone));
        return zone;
    }

    /// <summary>Runs a change through the host's context (with its interceptors) and saves it.</summary>
    public static async Task ChangeAsync(SqlServerApiFactory api, Action<OmtsDbContext> change)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        change(db);
        await db.SaveChangesAsync();
    }

    public static async Task<T> QueryAsync<T>(SqlServerApiFactory api, Func<OmtsDbContext, Task<T>> query)
    {
        using var scope = api.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<OmtsDbContext>());
    }
}
