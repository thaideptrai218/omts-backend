using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Infrastructure.Persistence.Configurations.Farming;

internal sealed class ZoneComplianceDocumentConfiguration : IEntityTypeConfiguration<ZoneComplianceDocument>
{
    public void Configure(EntityTypeBuilder<ZoneComplianceDocument> builder)
    {
        builder.Property(x => x.DocumentType).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.DocumentNumber).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.Holder).HasMaxLength(Lengths.Name);
        builder.Property(x => x.IssuingAuthority).HasMaxLength(Lengths.Name);
        builder.Property(x => x.LegalScope).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.EvidenceFileUrl).HasMaxLength(Lengths.Url);
        builder.HasIndex(x => x.ExpiryDate);

        builder.HasOne(x => x.ReplacesDocument).WithMany().HasForeignKey(x => x.ReplacesDocumentId);
        // A license is replaced at most once: parallel replaces cannot both succeed (FARM-03).
        builder.HasIndex(x => x.ReplacesDocumentId).IsUnique();
    }
}
