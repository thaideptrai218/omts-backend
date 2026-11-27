using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Configurations.Identity;

internal sealed class LandingPhotoConfiguration : IEntityTypeConfiguration<LandingPhoto>
{
    public void Configure(EntityTypeBuilder<LandingPhoto> builder)
    {
        builder.Property(x => x.StorageKey).HasMaxLength(Lengths.Url);
        builder.Property(x => x.Caption).HasMaxLength(Lengths.Name);
        builder.HasIndex(x => new { x.Section, x.SortOrder });
    }
}
