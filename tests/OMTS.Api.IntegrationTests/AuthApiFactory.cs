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

/// <summary>Runs the real API with an in-memory database and a test signing key.</summary>
public sealed class AuthApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"omts-auth-{Guid.NewGuid()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Jwt:SigningKey", "test-signing-key-that-is-long-enough-for-hs256-0123456789");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<OmtsDbContext>>();
            services.AddDbContext<OmtsDbContext>(options => options.UseInMemoryDatabase(_databaseName));
        });
    }

    public async Task<Account> AddAccountAsync(
        string username,
        string password,
        SubjectType subjectType = SubjectType.CooperativeOwner,
        Action<Account>? configure = null)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

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

        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account;
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
