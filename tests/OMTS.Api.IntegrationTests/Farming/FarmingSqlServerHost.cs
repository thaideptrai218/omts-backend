using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// A migrated SQL Server database with the real API, a cooperative, a logged-in Owner and its own Local storage
/// folder; everything is removed on dispose. For <see cref="SqlServerFactAttribute"/> tests only.
/// </summary>
public sealed class FarmingSqlServerHost : IAsyncDisposable
{
    private FarmingSqlServerHost(SqlServerTestDatabase database, SqlServerApiFactory api, string storageRoot)
    {
        Database = database;
        Api = api;
        StorageRoot = storageRoot;
    }

    public SqlServerTestDatabase Database { get; }
    public SqlServerApiFactory Api { get; }
    public string StorageRoot { get; }
    public Guid CooperativeId { get; private set; }
    public Actor Owner { get; private set; } = null!;

    public static async Task<FarmingSqlServerHost> StartAsync()
    {
        var database = SqlServerTestDatabase.Create();
        await using (var db = database.CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        var storageRoot = Path.Combine(Path.GetTempPath(), $"omts-files-{Guid.NewGuid():N}");
        var api = new SqlServerApiFactory(database, new Dictionary<string, string?> { ["Storage:Local:RootPath"] = storageRoot });
        var host = new FarmingSqlServerHost(database, api, storageRoot);
        var cooperative = new Cooperative { FullName = "HTX Trung Nam" };
        await host.UseDbAsync(db => db.Cooperatives.Add(cooperative));
        host.CooperativeId = cooperative.Id;
        var owner = await api.AddAccountAsync(NewCode(), Password);
        host.Owner = new Actor(await host.LoginAsync(owner.Username), owner);
        return host;
    }

    public async Task<HttpClient> LoginAsync(string username)
    {
        var client = Api.CreateClient();
        var tokens = await LoginTokensAsync(client, username);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    /// <summary>Logged-in staff (verified phone) with one effective grant in the zone of a role viewing cultivation cycles.</summary>
    public async Task<Actor> StaffInZoneAsync(Guid zoneId)
    {
        using var scope = Api.Services.CreateScope();
        var staff = new Account
        {
            Username = NewCode(), DisplayName = "Zone manager", SubjectType = SubjectType.Staff, StatusId = StatusIds.Account.Active,
            PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password),
            PhoneNumber = NewPhone(), PhoneVerifiedAt = DateTimeOffset.UtcNow,
        };
        var role = new Role { Name = NewCode() };
        role.Permissions.Add(new RolePermission { FunctionCode = "CULTIVATION_CYCLES", CanView = true });
        var grant = new AccessGrant
        {
            AccountId = staff.Id, RoleId = role.Id, ZoneId = zoneId, ScopeType = AccessScopeType.SpecificZone,
            EffectiveFrom = FarmingTestKit.Today.AddDays(-1), GrantedByAccountId = Owner.Account.Id,
        };
        await UseDbAsync(db =>
        {
            db.Accounts.Add(staff);
            db.Roles.Add(role);
            db.AccessGrants.Add(grant);
        });
        return new Actor(await LoginAsync(staff.Username), staff, grant);
    }

    public async Task UseDbAsync(Action<OmtsDbContext> change)
    {
        using var scope = Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        change(db);
        await db.SaveChangesAsync();
    }

    public async Task<T> QueryAsync<T>(Func<OmtsDbContext, Task<T>> query)
    {
        using var scope = Api.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<OmtsDbContext>());
    }

    /// <summary>Storage keys of the files kept under a category folder.</summary>
    public IReadOnlyList<string> StoredKeys(string category)
    {
        var folder = Path.Combine(StorageRoot, category);
        return Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(StorageRoot, f).Replace('\\', '/')).Order().ToList()
            : [];
    }

    public FarmingZone NewZoneEntity(string code) =>
        new() { Code = code, Name = $"Zone {code}", CooperativeId = CooperativeId };

    public async ValueTask DisposeAsync()
    {
        await Api.DisposeAsync();
        await Database.DisposeAsync();
        if (Directory.Exists(StorageRoot))
        {
            Directory.Delete(StorageRoot, recursive: true);
        }
    }
}
