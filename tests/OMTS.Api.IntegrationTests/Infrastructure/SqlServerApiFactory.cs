using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Runs the real API on a real SQL Server database (rowversion, constraints, transactions), for the
/// behaviour the in-memory provider cannot show. Use only from <see cref="SqlServerFactAttribute"/> tests.
/// </summary>
public sealed class SqlServerApiFactory(SqlServerTestDatabase database, IReadOnlyDictionary<string, string?>? settings = null)
    : WebApplicationFactory<Program>
{
    private readonly string _signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        // Added last, so it wins over a developer's user-secrets.
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:OmtsDb"] = database.ConnectionString,
            ["Jwt:SigningKey"] = _signingKey,
            ["RateLimits:CodeRequestsPerMinute"] = "100000",
            ["RateLimits:LoginsPerMinute"] = "100000",
        }.Concat(settings ?? new Dictionary<string, string?>())));

    public async Task<Account> AddAccountAsync(string username, string password, SubjectType subjectType = SubjectType.CooperativeOwner)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var account = new Account
        {
            Username = username,
            DisplayName = username,
            PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(password),
            SubjectType = subjectType,
            StatusId = StatusIds.Account.Active,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }
}
