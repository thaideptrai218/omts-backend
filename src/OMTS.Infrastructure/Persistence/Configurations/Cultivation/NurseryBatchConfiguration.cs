using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Infrastructure.Persistence.Configurations.Cultivation;

internal sealed class NurseryBatchConfiguration : IEntityTypeConfiguration<NurseryBatch>
{
    public void Configure(EntityTypeBuilder<NurseryBatch> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasOne(x => x.Zone).WithMany().HasForeignKey(x => x.ZoneId);
        builder.HasOne(x => x.SeedLotLine).WithMany().HasForeignKey(x => x.SeedLotLineId);
        builder.HasOne(x => x.Status).WithMany().HasForeignKey(x => x.StatusId);
        builder.HasOne(x => x.Issue).WithMany().HasForeignKey(x => x.IssueId);
        builder.ReferencesAccount(nameof(NurseryBatch.CompletedByAccountId));
        builder.ReferencesAccount(nameof(NurseryBatch.ClosedByAccountId));
        builder.Property(x => x.QuantityIn).HasPrecision(18, 3);
        builder.Property(x => x.Survivors).HasPrecision(18, 3);
        builder.Property(x => x.CloseLossPieces).HasPrecision(18, 3);
        builder.Property(x => x.CloseReason).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.Note).HasMaxLength(NurseryBatch.MaxNoteLength);
        builder.Property(x => x.CancelReason).HasMaxLength(Lengths.Reason);
        builder.HasIndex(x => new { x.ZoneId, x.StatusId, x.StartDate });
        builder.HasIndex(x => x.SeedLotLineId);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_NurseryBatch_QuantityIn", "[QuantityIn] > 0");
            t.HasCheckConstraint("CK_NurseryBatch_Survivors", "[Survivors] IS NULL OR ([Survivors] >= 0 AND [Survivors] <= [QuantityIn])");
        });
    }
}

internal sealed class NurseryBatchAllocationConfiguration : IEntityTypeConfiguration<NurseryBatchAllocation>
{
    public void Configure(EntityTypeBuilder<NurseryBatchAllocation> builder)
    {
        builder.HasOne(x => x.NurseryBatch).WithMany(b => b.Allocations).HasForeignKey(x => x.NurseryBatchId);
        builder.HasOne(x => x.Raft).WithMany().HasForeignKey(x => x.RaftId);
        builder.HasOne(x => x.Cycle).WithMany().HasForeignKey(x => x.CycleId);
        builder.Property(x => x.Quantity).HasPrecision(18, 3);

        // A raft appears once in a batch, and a cycle belongs to one allocation.
        builder.HasIndex(x => new { x.NurseryBatchId, x.RaftId }).IsUnique();
        builder.HasIndex(x => x.CycleId).IsUnique();
        builder.ToTable(t => t.HasCheckConstraint("CK_NurseryBatchAllocation_Quantity", "[Quantity] > 0"));
    }
}

internal sealed class NurseryBatchPhotoConfiguration : IEntityTypeConfiguration<NurseryBatchPhoto>
{
    public void Configure(EntityTypeBuilder<NurseryBatchPhoto> builder)
    {
        builder.HasOne(x => x.NurseryBatch).WithMany(b => b.Photos).HasForeignKey(x => x.NurseryBatchId);
        builder.Property(x => x.FileKey).HasMaxLength(Lengths.Url);

        // One file per slot of each phase and only the allowed slots: no save can leave more than the maximum.
        builder.HasIndex(x => new { x.NurseryBatchId, x.Phase, x.SortOrder }).IsUnique();
        builder.ToTable(t => t.HasCheckConstraint("CK_NurseryBatchPhoto_SortOrder", $"[SortOrder] BETWEEN 1 AND {NurseryBatch.MaxPhotos}"));
    }
}
