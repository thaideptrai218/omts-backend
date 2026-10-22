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

    private async Task<Role> LoadAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OmtsDbContext>().Roles.AsNoTracking().SingleAsync(r => r.Id == id);
    }
}
