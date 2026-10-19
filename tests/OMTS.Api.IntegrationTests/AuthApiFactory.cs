using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Application.Authentication;
using OMTS.Domain.Entities;
using OMTS.Domain.Enums;
using OMTS.Domain.Statuses;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests;

/// <summary>Runs the real API with an in-memory database, a test signing key and test-only endpoints.</summary>
public sealed class AuthApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"omts-auth-{Guid.NewGuid()}";

    // Generated per test host so no key-like literal is committed (gitleaks).
    private readonly string _signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Jwt:SigningKey", _signingKey);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<OmtsDbContext>>();
            services.AddDbContext<OmtsDbContext>((provider, options) => options
                .UseInMemoryDatabase(_databaseName)
                .AddInterceptors(provider.GetRequiredService<PermissionCacheInvalidationInterceptor>()));

            services.AddControllers().AddApplicationPart(typeof(AuthApiFactory).Assembly);
        });
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
    public async Task<AccessGrant> GrantAsync(Account account, Role role, FarmingZone? zone)
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

internal static class ServiceCollectionTestExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(T)).ToList())
        {
            services.Remove(descriptor);
        }
    }
}
