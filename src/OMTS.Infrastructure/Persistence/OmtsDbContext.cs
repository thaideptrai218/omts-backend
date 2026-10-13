using Microsoft.EntityFrameworkCore;

namespace OMTS.Infrastructure.Persistence;

public sealed class OmtsDbContext(DbContextOptions<OmtsDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OmtsDbContext).Assembly);
    }
}
