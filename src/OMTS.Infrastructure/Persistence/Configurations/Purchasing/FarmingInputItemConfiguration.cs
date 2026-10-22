using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Infrastructure.Persistence.Configurations.Purchasing;

internal sealed class FarmingInputItemConfiguration : IEntityTypeConfiguration<FarmingInputItem>
{
    public void Configure(EntityTypeBuilder<FarmingInputItem> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.Property(x => x.Name).HasMaxLength(Lengths.Name);
        builder.Property(x => x.Unit).HasMaxLength(30);
        builder.Property(x => x.Description).HasMaxLength(Lengths.LongText);
        builder.Property(x => x.IllustrationUrl).HasMaxLength(Lengths.Url);
    }
}
