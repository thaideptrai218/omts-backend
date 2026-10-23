using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Infrastructure.Persistence.Configurations.Farming;

internal sealed class RaftMaterialRecordConfiguration : IEntityTypeConfiguration<RaftMaterialRecord>
{
    public void Configure(EntityTypeBuilder<RaftMaterialRecord> builder)
    {
        builder.HasOne(x => x.Raft).WithMany(r => r.MaterialRecords).HasForeignKey(x => x.RaftId);
        builder.HasOne(x => x.RecordedByAccount).WithMany().HasForeignKey(x => x.RecordedByAccountId);
        builder.Property(x => x.ReplacementReason).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.PurchasePlaceNotes).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.InvoicePhotoUrl).HasMaxLength(Lengths.Url);

        // Exactly one Raft Creation record per raft (BR-27).
        builder.HasIndex(x => x.RaftId)
            .IsUnique()
            .HasFilter("[RecordType] = N'RaftCreation'")
            .HasDatabaseName("UX_RaftMaterialRecords_RaftId_RaftCreation");
        builder.HasThousandVndCheck(nameof(RaftMaterialRecord.TotalAmount));
    }
}
