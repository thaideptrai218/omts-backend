using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Infrastructure.Persistence.Configurations.Farming;

internal sealed class RaftConfiguration : IEntityTypeConfiguration<Raft>
{
    public void Configure(EntityTypeBuilder<Raft> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => new { x.ZoneId, x.Code }).IsUnique();
        builder.Property(x => x.Location).HasMaxLength(Lengths.Address);
        builder.Property(x => x.Latitude).HasPrecision(9, 6);
        builder.Property(x => x.Longitude).HasPrecision(9, 6);
        builder.HasCoordinateChecks();
        builder.Property(x => x.Size).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.PhotoUrl).HasMaxLength(Lengths.Url);
        builder.Property(x => x.ConditionNotes).HasMaxLength(Lengths.LongText);

        builder.HasOne(x => x.Zone).WithMany(z => z.Rafts).HasForeignKey(x => x.ZoneId);
    }
}
