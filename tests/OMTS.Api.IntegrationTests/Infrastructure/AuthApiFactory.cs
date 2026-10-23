using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Infrastructure;

/// <summary>Runs the real API with an in-memory database, a test signing key and test-only endpoints.</summary>
public class AuthApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"omts-auth-{Guid.NewGuid()}";

    // Generated per test host so no key-like literal is committed (gitleaks).
    private readonly string _signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    /// <summary>Folder of the Local storage provider for this host; deleted with the host.</summary>
    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), $"omts-files-{Guid.NewGuid():N}");

    /// <summary>Every log line the host wrote.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Jwt:SigningKey", _signingKey);
        builder.UseSetting("Storage:Local:RootPath", StorageRoot);
        builder.UseSetting("Storage:Local:SigningKey", _signingKey);
        // Every test client shares one IP; RateLimitTests lowers these to check the limiter itself.
        builder.UseSetting("RateLimits:CodeRequestsPerMinute", "100000");
        builder.UseSetting("RateLimits:LoginsPerMinute", "100000");
        // Background jobs never run on their own in tests; tests call them directly.
        builder.UseSetting("Jobs:ZoneLicenseReminder:CatchUpOnStart", "false");
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<OmtsDbContext>>();
            services.AddDbContext<OmtsDbContext>((provider, options) => options
                .UseInMemoryDatabase(_databaseName)
                .AddOmtsInterceptors(provider));

            services.AddControllers().AddApplicationPart(typeof(AuthApiFactory).Assembly);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(StorageRoot))
        {
            Directory.Delete(StorageRoot, recursive: true);
        }
    }

    public async Task<Account> AddAccountAsync(
        string username,
        string password,
        SubjectType subjectType = SubjectType.CooperativeOwner,
        Action<Account>? configure = null)
    {
        var hasher = Services.GetRequiredService<IPasswordHasher>();
        var account = new Account
        {
            Username = username,
            DisplayName = username,
            PasswordHash = hasher.Hash(password),
            SubjectType = subjectType,
            PhoneNumber = $"09{Random.Shared.Next(10000000, 99999999)}",
            PhoneVerifiedAt = DateTimeOffset.UtcNow,
            StatusId = StatusIds.Account.Active,
        };
        configure?.Invoke(account);

        await UseDbAsync(db => db.Accounts.Add(account));
        return account;
    }

    public async Task<FarmingZone> AddZoneAsync(string code)
    {
        var zone = new FarmingZone { Code = code, Name = $"Zone {code}" };
        await UseDbAsync(db => db.FarmingZones.Add(zone));
        return zone;
    }

    /// <summary>Creates a role whose permissions are the given function → actions.</summary>
    public async Task<Role> AddRoleAsync(string name, params (string Function, bool View, bool Create, bool Edit)[] permissions)
    {
        var role = new Role { Name = name };
        foreach (var (function, view, create, edit) in permissions)
        {
            role.Permissions.Add(new RolePermission { FunctionCode = function, CanView = view, CanCreate = create, CanEdit = edit });
        }

        await UseDbAsync(db => db.Roles.Add(role));
        return role;
    }

    /// <summary>Grants <paramref name="role"/> in <paramref name="zone"/>, or in All zones when null.</summary>
    public async Task<AccessGrant> GrantAsync(Account account, Role role, FarmingZone? zone, Action<AccessGrant>? configure = null)
    {
        var grant = new AccessGrant
        {
            AccountId = account.Id,
            RoleId = role.Id,
            ZoneId = zone?.Id,
            ScopeType = zone is null ? AccessScopeType.AllZones : AccessScopeType.SpecificZone,
            EffectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1),
            GrantedByAccountId = account.Id,
        };
        configure?.Invoke(grant);
        await UseDbAsync(db => db.AccessGrants.Add(grant));
        return grant;
    }

    /// <summary>Runs a change through a fresh context and saves it, as UC11/UC12 would.</summary>
    public async Task UseDbAsync(Action<OmtsDbContext> change)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        change(db);
        await db.SaveChangesAsync();
    }

    public async Task UpdateAsync<T>(Guid id, Action<T> change) where T : class
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var entity = await db.Set<T>().FindAsync(id) ?? throw new InvalidOperationException($"{typeof(T).Name} {id} not found.");
        change(entity);
        await db.SaveChangesAsync();
    }
}
