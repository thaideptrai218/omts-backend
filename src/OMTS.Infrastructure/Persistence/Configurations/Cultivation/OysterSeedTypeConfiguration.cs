using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Infrastructure.Persistence.Configurations.Cultivation;

internal sealed class OysterSeedTypeConfiguration : IEntityTypeConfiguration<OysterSeedType>
{
    public void Configure(EntityTypeBuilder<OysterSeedType> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.Property(x => x.Name).HasMaxLength(Lengths.Name);
        builder.Property(x => x.Description).HasMaxLength(Lengths.LongText);
        builder.Property(x => x.IllustrationUrl).HasMaxLength(Lengths.Url);
    }
}
