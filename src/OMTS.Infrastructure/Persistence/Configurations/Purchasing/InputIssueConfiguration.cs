using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Infrastructure.Persistence.Configurations.Purchasing;

internal sealed class InputIssueTypeConfiguration : IEntityTypeConfiguration<InputIssueType>
{
    /// <summary>The seeded types carry this creation time; they are made by the system, not by a person.</summary>
    private static readonly DateTimeOffset SeededAt = new(2026, 11, 11, 0, 0, 0, TimeSpan.FromHours(7));

    public void Configure(EntityTypeBuilder<InputIssueType> builder)
    {
        builder.Property(x => x.Name).HasMaxLength(100);
        builder.HasIndex(x => new { x.IsActive, x.Name });
        builder.HasData(
            Seed(InputIssueTypeIds.SeedRelease, "Thả giống"),
            Seed(InputIssueTypeIds.RaftRepair, "Sửa chữa bè"),
            Seed(InputIssueTypeIds.ZoneShared, "Dùng chung cho vùng"),
            Seed(InputIssueTypeIds.Nursery, "Ươm giống"));
    }

    // Anonymous objects: the key has no public setter.
    private static object Seed(Guid id, string name) => new
    {
        Id = id,
        Name = name,
        IsSystem = true,
        IsActive = true,
        CreatedAt = SeededAt,
        // The in-memory test provider needs a value for the row version; SQL Server generates its own and takes none.
        RowVersion = Array.Empty<byte>(),
    };
}

internal sealed class InputIssueConfiguration : IEntityTypeConfiguration<InputIssue>
{
    public void Configure(EntityTypeBuilder<InputIssue> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasOne(x => x.Zone).WithMany().HasForeignKey(x => x.ZoneId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Raft).WithMany().HasForeignKey(x => x.RaftId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.IssueType).WithMany().HasForeignKey(x => x.IssueTypeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Receiver).WithMany().HasForeignKey(x => x.ReceiverEmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.Reason).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.Notes).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.CancelReason).HasMaxLength(Lengths.Reason);
        builder.HasIndex(x => new { x.Kind, x.IssuedAt });
        builder.HasIndex(x => x.ZoneId);
        builder.HasIndex(x => new { x.SourceKind, x.SourceRecordId });
    }
}

internal sealed class InputIssueLineConfiguration : IEntityTypeConfiguration<InputIssueLine>
{
    public void Configure(EntityTypeBuilder<InputIssueLine> builder)
    {
        builder.HasOne(x => x.Issue).WithMany(i => i.Lines).HasForeignKey(x => x.IssueId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.IssueId, x.LineNumber }).IsUnique();
        builder.HasOne(x => x.Item).WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ItemUnit).WithMany().HasForeignKey(x => x.ItemUnitId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.SeedLotLine).WithMany().HasForeignKey(x => x.SeedLotLineId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.Unit).HasMaxLength(30);
        builder.Property(x => x.UnitFactor).HasPrecision(18, 3);
        builder.Property(x => x.EnteredQuantity).HasPrecision(18, 3);
        builder.Property(x => x.Quantity).HasPrecision(18, 5);
        builder.Property(x => x.UnitCost).HasPrecision(18, 4);
        builder.Property(x => x.Value).HasPrecision(18, 0);
        builder.Property(x => x.Note).HasMaxLength(Lengths.Url);
    }
}
