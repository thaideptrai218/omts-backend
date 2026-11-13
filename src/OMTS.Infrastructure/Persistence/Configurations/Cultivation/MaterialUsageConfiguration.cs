using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Infrastructure.Persistence.Configurations.Cultivation;

internal sealed class MaterialUsageConfiguration : IEntityTypeConfiguration<MaterialUsage>
{
    public void Configure(EntityTypeBuilder<MaterialUsage> builder)
    {
        builder.HasOne(x => x.Raft).WithMany().HasForeignKey(x => x.RaftId);
        builder.HasOne(x => x.Item).WithMany().HasForeignKey(x => x.ItemId);
        builder.HasOne(x => x.Issue).WithMany().HasForeignKey(x => x.IssueId).OnDelete(DeleteBehavior.Restrict);
        builder.ReferencesAccount(nameof(MaterialUsage.RecordedByAccountId));
        builder.Property(x => x.RepairReason).HasMaxLength(MaterialUsage.MaxRepairReasonLength);
        builder.Property(x => x.Quantity).HasPrecision(18, 3);
        builder.HasIndex(x => new { x.RaftId, x.UsageDate });
        builder.ToTable(t => t.HasCheckConstraint("CK_MaterialUsage_Quantity", "[Quantity] > 0"));
    }
}
