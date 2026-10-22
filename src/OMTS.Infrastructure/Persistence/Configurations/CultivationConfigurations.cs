using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities;
using OMTS.Domain.Statuses;

namespace OMTS.Infrastructure.Persistence.Configurations;

internal sealed class OysterSeedTypeConfiguration : IEntityTypeConfiguration<OysterSeedType>
{
    public void Configure(EntityTypeBuilder<OysterSeedType> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.Property(x => x.Name).HasMaxLength(Lengths.Name);
        builder.Property(x => x.Description).HasMaxLength(Lengths.LongText);
        builder.Property(x => x.IllustrationUrl).HasMaxLength(Lengths.Url);
    }
}

internal sealed class SeedSupplierConfiguration : IEntityTypeConfiguration<SeedSupplier>
{
    public void Configure(EntityTypeBuilder<SeedSupplier> builder)
    {
        builder.Property(x => x.Name).HasMaxLength(Lengths.Name);
        builder.Property(x => x.PhoneNumber).HasMaxLength(Lengths.Phone);
        builder.Property(x => x.OtherContacts).HasMaxLength(Lengths.Address);
    }
}

internal sealed class SeedReceiptLotConfiguration : IEntityTypeConfiguration<SeedReceiptLot>
{
    public void Configure(EntityTypeBuilder<SeedReceiptLot> builder)
    {
        builder.HasOne(x => x.SeedType).WithMany().HasForeignKey(x => x.SeedTypeId);
        builder.HasOne(x => x.Supplier).WithMany(s => s.ReceiptLots).HasForeignKey(x => x.SupplierId);
        builder.Property(x => x.InternalCode).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.InternalCode).IsUnique();
        builder.Property(x => x.SupplierLotCode).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.ActualSource).HasMaxLength(Lengths.Address);
        builder.Property(x => x.Quantity).HasPrecision(18, 3);
        builder.Property(x => x.Unit).HasMaxLength(30);
    }
}

internal sealed class CultivationCycleConfiguration : IEntityTypeConfiguration<CultivationCycle>
{
    public void Configure(EntityTypeBuilder<CultivationCycle> builder)
    {
        builder.HasOne(x => x.Raft).WithMany(r => r.CultivationCycles).HasForeignKey(x => x.RaftId);
        builder.HasOne(x => x.PlannedSeedType).WithMany().HasForeignKey(x => x.PlannedSeedTypeId);
        builder.Property(x => x.SeedingCompletionReason).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.ClosingSummary).HasMaxLength(Lengths.LongText);

        // A raft holds at most one Open cycle; a new one starts only after closing (BR-21).
        builder.HasIndex(x => x.RaftId)
            .IsUnique()
            .HasFilter($"[StatusId] = {StatusIds.CultivationCycle.Open}")
            .HasDatabaseName("UX_CultivationCycles_RaftId_Open");
    }
}

internal sealed class SeedingEventConfiguration : IEntityTypeConfiguration<SeedingEvent>
{
    public void Configure(EntityTypeBuilder<SeedingEvent> builder)
    {
        builder.HasOne(x => x.Cycle).WithMany(c => c.SeedingEvents).HasForeignKey(x => x.CycleId);
        builder.HasOne(x => x.SeedLot).WithMany().HasForeignKey(x => x.SeedLotId);
        builder.ReferencesAccount(nameof(SeedingEvent.RecordedByAccountId));
        builder.Property(x => x.Workers).HasMaxLength(Lengths.Address);
        builder.Property(x => x.SeedQuantity).HasPrecision(18, 3);
        builder.Property(x => x.PlanDeviationReason).HasMaxLength(Lengths.Reason);
    }
}

internal sealed class LaneSegmentConfiguration : IEntityTypeConfiguration<LaneSegment>
{
    public void Configure(EntityTypeBuilder<LaneSegment> builder)
    {
        builder.HasOne(x => x.Cycle).WithMany(c => c.LaneSegments).HasForeignKey(x => x.CycleId);
        builder.HasOne(x => x.Lane).WithMany(l => l.Segments).HasForeignKey(x => x.LaneId);
        builder.HasOne(x => x.Seeding).WithMany(s => s.SeededSegments).HasForeignKey(x => x.SeedingId);
        builder.HasOne(x => x.Order).WithMany(o => o.PlannedHarvestSegments).HasForeignKey(x => x.OrderId);
        builder.HasOne(x => x.HarvestEvent).WithMany(h => h.HarvestedSegments).HasForeignKey(x => x.HarvestEventId);
        builder.HasOne(x => x.Lot).WithMany(l => l.SourceSegments).HasForeignKey(x => x.LotId);
        builder.HasOne(x => x.Log).WithMany().HasForeignKey(x => x.LogId);
        builder.ReferencesAccount(nameof(LaneSegment.RecordedByAccountId), nameof(LaneSegment.ConfirmedByAccountId));

        builder.Property(x => x.FromPosition).HasPrecision(10, 2);
        builder.Property(x => x.ToPosition).HasPrecision(10, 2);
        builder.Property(x => x.PlannedKg).HasPrecision(18, 3);
        builder.Property(x => x.WriteOffReason).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.WriteOffPhotoUrl).HasMaxLength(Lengths.Url);
        builder.Property(x => x.PlanDeviationReason).HasMaxLength(Lengths.Reason);

        builder.HasIndex(x => new { x.CycleId, x.LaneId, x.SegmentType });
        builder.ToTable(t => t.HasCheckConstraint("CK_LaneSegment_Positions", "[ToPosition] > [FromPosition] AND [FromPosition] >= 0"));
    }
}

internal sealed class CultivationLogEntryConfiguration : IEntityTypeConfiguration<CultivationLogEntry>
{
    public void Configure(EntityTypeBuilder<CultivationLogEntry> builder)
    {
        builder.HasOne(x => x.Zone).WithMany().HasForeignKey(x => x.ZoneId);
        builder.HasOne(x => x.Cycle).WithMany(c => c.LogEntries).HasForeignKey(x => x.CycleId);
        builder.HasOne(x => x.Lane).WithMany().HasForeignKey(x => x.LaneId);
        builder.ReferencesAccount(nameof(CultivationLogEntry.RecordedByAccountId));
        builder.Property(x => x.LanePosition).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.EventType).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.Observation).HasMaxLength(Lengths.LongText);
        builder.Property(x => x.Observer).HasMaxLength(Lengths.Name);
        builder.HasIndex(x => new { x.ZoneId, x.OccurredAt });
    }
}

internal sealed class WaterMeasurementConfiguration : IEntityTypeConfiguration<WaterMeasurement>
{
    public void Configure(EntityTypeBuilder<WaterMeasurement> builder)
    {
        builder.HasOne(x => x.Zone).WithMany(z => z.WaterMeasurements).HasForeignKey(x => x.ZoneId);
        builder.HasOne(x => x.CorrectedFromMeasurement).WithMany().HasForeignKey(x => x.CorrectedFromMeasurementId);
        builder.ReferencesAccount(nameof(WaterMeasurement.MeasuredByAccountId), nameof(WaterMeasurement.ConfirmedByAccountId));

        builder.Property(x => x.MeasurementPoint).HasMaxLength(Lengths.Name);
        builder.Property(x => x.PhotoUrl).HasMaxLength(Lengths.Url);
        builder.Property(x => x.ViolatedValues).HasMaxLength(Lengths.Name);
        builder.Property(x => x.CorrectionReason).HasMaxLength(Lengths.Reason);
        foreach (var ph in new[] { nameof(WaterMeasurement.Ph), nameof(WaterMeasurement.OcrPh), nameof(WaterMeasurement.PhMinUsed), nameof(WaterMeasurement.PhMaxUsed) })
        {
            builder.Property(ph).HasPrecision(4, 2);
        }

        foreach (var salinity in new[] { nameof(WaterMeasurement.Salinity), nameof(WaterMeasurement.OcrSalinity), nameof(WaterMeasurement.SalinityMinUsed), nameof(WaterMeasurement.SalinityMaxUsed) })
        {
            builder.Property(salinity).HasPrecision(5, 2);
        }

        builder.HasIndex(x => new { x.ZoneId, x.MeasuredAt });
    }
}
