using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Infrastructure.Persistence.Configurations.Farming;

internal sealed class LaneConfiguration : IEntityTypeConfiguration<Lane>
{
    public void Configure(EntityTypeBuilder<Lane> builder)
    {
        builder.HasOne(x => x.Raft).WithMany(r => r.Lanes).HasForeignKey(x => x.RaftId);
        builder.HasIndex(x => new { x.RaftId, x.LaneNumber }).IsUnique();
        builder.Property(x => x.LineLengthM).HasPrecision(7, 2);
        builder.Ignore(x => x.IsOutOfService);
        builder.Ignore(x => x.IsUsable);
        builder.ToTable(t => t.HasCheckConstraint("CK_Lane_LineLength", "[LineLengthM] IS NULL OR [LineLengthM] > 0"));
    }
}

internal sealed class LaneIncidentConfiguration : IEntityTypeConfiguration<LaneIncident>
{
    public void Configure(EntityTypeBuilder<LaneIncident> builder)
    {
        builder.HasOne(x => x.Lane).WithMany().HasForeignKey(x => x.LaneId);
        builder.HasOne<OMTS.Domain.Entities.Cultivation.CultivationCycle>().WithMany().HasForeignKey(x => x.CycleId);
        builder.HasOne<LaneIncident>().WithMany().HasForeignKey(x => x.ResolvesIncidentId);
        builder.ReferencesAccount(nameof(LaneIncident.RecordedByAccountId));
        builder.Property(x => x.ReasonCode).HasMaxLength(Lengths.Code);
        builder.Property(x => x.Reason).HasMaxLength(LaneIncident.MaxReasonLength);
        builder.Property(x => x.StateBefore).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => new { x.LaneId, x.OccurredAt });
        builder.HasIndex(x => x.CycleId);
        builder.ToTable(t => t.HasCheckConstraint("CK_LaneIncident_Reason", "LEN([Reason]) > 0"));
    }
}

internal sealed class LaneIncidentPhotoConfiguration : IEntityTypeConfiguration<LaneIncidentPhoto>
{
    public void Configure(EntityTypeBuilder<LaneIncidentPhoto> builder)
    {
        builder.HasOne(x => x.Incident).WithMany(i => i.Photos).HasForeignKey(x => x.IncidentId);
        builder.Property(x => x.FileKey).HasMaxLength(Lengths.Url);

        // One file per slot and only the allowed slots: parallel uploads can never leave more than the maximum.
        builder.HasIndex(x => new { x.IncidentId, x.SortOrder }).IsUnique();
        builder.ToTable(t => t.HasCheckConstraint("CK_LaneIncidentPhoto_SortOrder", $"[SortOrder] BETWEEN 1 AND {LaneIncident.MaxPhotos}"));
    }
}

internal sealed class LaneIncidentMaterialConfiguration : IEntityTypeConfiguration<LaneIncidentMaterial>
{
    public void Configure(EntityTypeBuilder<LaneIncidentMaterial> builder)
    {
        builder.HasOne(x => x.Incident).WithMany(i => i.Materials).HasForeignKey(x => x.IncidentId);
        builder.HasOne<OMTS.Domain.Entities.Cultivation.MaterialUsage>().WithMany().HasForeignKey(x => x.MaterialUsageId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<OMTS.Domain.Entities.Purchasing.FarmingInputItem>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<OMTS.Domain.Entities.Purchasing.InputIssue>().WithMany().HasForeignKey(x => x.IssueId).OnDelete(DeleteBehavior.NoAction);
        builder.Property(x => x.Quantity).HasPrecision(18, 3);
    }
}
