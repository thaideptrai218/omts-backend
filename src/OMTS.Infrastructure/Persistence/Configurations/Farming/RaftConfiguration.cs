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
        // Numeric geometry (12/11/2026); the area is worked out in code, never stored. Null only for a raft whose old size text could not be read.
        builder.Property(x => x.LengthM).HasPrecision(7, 2);
        builder.Property(x => x.WidthM).HasPrecision(7, 2);
        builder.Ignore(x => x.AreaM2);
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_Raft_Geometry", "([LengthM] IS NULL OR [LengthM] BETWEEN 1 AND 500) AND ([WidthM] IS NULL OR [WidthM] BETWEEN 1 AND 200)"));
        builder.Property(x => x.ConditionNotes).HasMaxLength(Lengths.LongText);

        builder.HasOne(x => x.Zone).WithMany(z => z.Rafts).HasForeignKey(x => x.ZoneId);
    }
}
