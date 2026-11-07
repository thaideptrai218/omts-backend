using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Platform;
using OMTS.Infrastructure.Persistence.Seed;

namespace OMTS.Infrastructure.Persistence.Configurations.Platform;

internal sealed class StatusConfiguration : IEntityTypeConfiguration<Status>
{
    public void Configure(EntityTypeBuilder<Status> builder)
    {
        // Ids are fixed by the catalog (StatusIds), not generated.
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Group).HasMaxLength(Lengths.Code);
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => new { x.Group, x.Code }).IsUnique();
        builder.Property(x => x.DisplayName).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.Meaning).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.DisplayColor).HasMaxLength(Lengths.Phone);

        builder.HasData(StatusCatalogSeed.Statuses);
    }
}
