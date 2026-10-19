using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Infrastructure.Persistence.Configurations.Farming;

internal sealed class RaftMaterialLineConfiguration : IEntityTypeConfiguration<RaftMaterialLine>
{
    public void Configure(EntityTypeBuilder<RaftMaterialLine> builder)
    {
        builder.HasOne(x => x.Record).WithMany(r => r.Lines).HasForeignKey(x => x.RecordId);
        builder.Property(x => x.Position).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.Specification).HasMaxLength(Lengths.Name);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_RaftMaterialLine_Quantity", "[Quantity] > 0");
            t.HasCheckConstraint("CK_RaftMaterialLine_UnitPrice", "[UnitPrice] >= 0");
        });
        builder.HasThousandVndCheck(nameof(RaftMaterialLine.LineAmount));
    }
}
