using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Infrastructure.Persistence.Configurations.Cultivation;

internal sealed class CultivationLogPhotoConfiguration : IEntityTypeConfiguration<CultivationLogPhoto>
{
    public void Configure(EntityTypeBuilder<CultivationLogPhoto> builder)
    {
        builder.HasOne(x => x.Log).WithMany(l => l.Photos).HasForeignKey(x => x.LogId);
        builder.Property(x => x.FileKey).HasMaxLength(Lengths.Url);

        // One file per slot and only the allowed slots: no save can leave more than the maximum on a log.
        builder.HasIndex(x => new { x.LogId, x.SortOrder }).IsUnique();
        builder.ToTable(t => t.HasCheckConstraint("CK_CultivationLogPhoto_SortOrder", $"[SortOrder] BETWEEN 1 AND {CultivationLog.MaxPhotos}"));
    }
}
