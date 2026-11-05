using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Infrastructure.Persistence.Configurations.Farming;

internal sealed class RaftPhotoConfiguration : IEntityTypeConfiguration<RaftPhoto>
{
    public void Configure(EntityTypeBuilder<RaftPhoto> builder)
    {
        builder.HasOne(x => x.Raft).WithMany(r => r.Photos).HasForeignKey(x => x.RaftId);
        builder.Property(x => x.FileKey).HasMaxLength(Lengths.Url);

        // One file per slot and only the allowed slots: parallel uploads can never leave more than the maximum.
        builder.HasIndex(x => new { x.RaftId, x.SortOrder }).IsUnique();
        builder.ToTable(t => t.HasCheckConstraint("CK_RaftPhoto_SortOrder", $"[SortOrder] BETWEEN 1 AND {Raft.MaxPhotos}"));
    }
}
