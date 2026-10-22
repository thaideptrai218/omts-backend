using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Identity;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Persistence;

public class AuditableEntityStampTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Save_stamps_creation_once_and_every_update()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var role = await factory.AddRoleAsync($"Role {Guid.NewGuid():N}");
        var created = await LoadAsync(role.Id);

        await factory.UpdateAsync<Role>(role.Id, r => r.Description = "changed");
        var updated = await LoadAsync(role.Id);

        Assert.True(created.CreatedAt >= before);
        Assert.Null(created.UpdatedAt);
        Assert.Equal(created.CreatedAt, updated.CreatedAt);
        Assert.NotNull(updated.UpdatedAt);
        Assert.True(updated.UpdatedAt >= created.CreatedAt);
    }

    // Login bookkeeping (failure counter, last login) is not an edit of the account.
    [Fact]
    public async Task Login_does_not_restamp_the_account()
    {
        var account = await factory.AddAccountAsync($"stamp-{Guid.NewGuid():N}"[..20], "Secret123");
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/auth/login", new { identifier = account.Username, password = "Wrong123" });
        await client.PostAsJsonAsync("/api/auth/login", new { identifier = account.Username, password = "Secret123" });

        using var scope = factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<OmtsDbContext>().Accounts.AsNoTracking().SingleAsync(a => a.Id == account.Id);
        Assert.NotNull(stored.LastLoginAt);
        Assert.Null(stored.UpdatedAt);
    }

    private async Task<Role> LoadAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OmtsDbContext>().Roles.AsNoTracking().SingleAsync(r => r.Id == id);
    }
}
