using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Infrastructure.Persistence.Configurations.Farming;

internal sealed class RaftLayoutVersionConfiguration : IEntityTypeConfiguration<RaftLayoutVersion>
{
    public void Configure(EntityTypeBuilder<RaftLayoutVersion> builder)
    {
        builder.HasOne(x => x.Raft).WithMany(r => r.LayoutVersions).HasForeignKey(x => x.RaftId);
        builder.HasIndex(x => new { x.RaftId, x.VersionNumber }).IsUnique();
    }
}
