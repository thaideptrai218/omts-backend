using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities;

namespace OMTS.Infrastructure.Persistence.Configurations;

internal sealed class FarmingZoneConfiguration : IEntityTypeConfiguration<FarmingZone>
{
    public void Configure(EntityTypeBuilder<FarmingZone> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.Property(x => x.Name).HasMaxLength(Lengths.Name);
        builder.Property(x => x.Location).HasMaxLength(Lengths.Address);
        builder.Property(x => x.Latitude).HasPrecision(9, 6);
        builder.Property(x => x.Longitude).HasPrecision(9, 6);
        builder.Property(x => x.Area).HasPrecision(18, 4);
        builder.Property(x => x.WaterMeasurementPoint).HasMaxLength(Lengths.Name);
        builder.Property(x => x.PhMin).HasPrecision(4, 2);
        builder.Property(x => x.PhMax).HasPrecision(4, 2);
        builder.Property(x => x.SalinityMin).HasPrecision(5, 2);
        builder.Property(x => x.SalinityMax).HasPrecision(5, 2);

        builder.HasOne(x => x.Cooperative).WithMany(c => c.FarmingZones).HasForeignKey(x => x.CooperativeId);
    }
}

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
    }
}

internal sealed class DocumentZoneScopeConfiguration : IEntityTypeConfiguration<DocumentZoneScope>
{
    public void Configure(EntityTypeBuilder<DocumentZoneScope> builder)
    {
        builder.HasKey(x => new { x.DocumentId, x.ZoneId });
        builder.HasOne(x => x.Document).WithMany(d => d.ZoneScopes).HasForeignKey(x => x.DocumentId);
        builder.HasOne(x => x.Zone).WithMany(z => z.DocumentScopes).HasForeignKey(x => x.ZoneId);
    }
}

internal sealed class RaftConfiguration : IEntityTypeConfiguration<Raft>
{
    public void Configure(EntityTypeBuilder<Raft> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => new { x.ZoneId, x.Code }).IsUnique();
        builder.Property(x => x.Location).HasMaxLength(Lengths.Address);
        builder.Property(x => x.Size).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.PhotoUrl).HasMaxLength(Lengths.Url);
        builder.Property(x => x.ConditionNotes).HasMaxLength(Lengths.LongText);

        builder.HasOne(x => x.Zone).WithMany(z => z.Rafts).HasForeignKey(x => x.ZoneId);
    }
}

internal sealed class RaftLayoutVersionConfiguration : IEntityTypeConfiguration<RaftLayoutVersion>
{
    public void Configure(EntityTypeBuilder<RaftLayoutVersion> builder)
    {
        builder.HasOne(x => x.Raft).WithMany(r => r.LayoutVersions).HasForeignKey(x => x.RaftId);
        builder.HasIndex(x => new { x.RaftId, x.VersionNumber }).IsUnique();
    }
}

internal sealed class LaneConfiguration : IEntityTypeConfiguration<Lane>
{
    public void Configure(EntityTypeBuilder<Lane> builder)
    {
        builder.HasOne(x => x.Raft).WithMany(r => r.Lanes).HasForeignKey(x => x.RaftId);
        builder.HasIndex(x => new { x.RaftId, x.LaneNumber }).IsUnique();
        builder.Property(x => x.Length).HasPrecision(10, 2);
        builder.Property(x => x.MeasurementMarkers).HasMaxLength(Lengths.Address);
    }
}

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
