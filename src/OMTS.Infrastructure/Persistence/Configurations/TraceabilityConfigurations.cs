using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities;

namespace OMTS.Infrastructure.Persistence.Configurations;

internal sealed class HarvestEventConfiguration : IEntityTypeConfiguration<HarvestEvent>
{
    public void Configure(EntityTypeBuilder<HarvestEvent> builder)
    {
        builder.HasOne(x => x.Order).WithMany(o => o.HarvestEvents).HasForeignKey(x => x.OrderId);
        builder.HasOne(x => x.Zone).WithMany().HasForeignKey(x => x.ZoneId);
        builder.ReferencesAccount(nameof(HarvestEvent.RecordedByAccountId));
        builder.Property(x => x.EstimatedKg).HasPrecision(18, 3);
        builder.Property(x => x.LotPhotoUrl).HasMaxLength(Lengths.Url);
    }
}

internal sealed class TraceabilityLotConfiguration : IEntityTypeConfiguration<TraceabilityLot>
{
    public void Configure(EntityTypeBuilder<TraceabilityLot> builder)
    {
        builder.Property(x => x.LotCode).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.LotCode).IsUnique();
        builder.Property(x => x.QrCode).HasMaxLength(Lengths.ShortText);
        builder.HasIndex(x => x.QrCode).IsUnique();

        // One first-sale order creates at most one lot (BR-09).
        builder.HasOne(x => x.InitialOrder).WithMany().HasForeignKey(x => x.InitialOrderId);
        builder.HasIndex(x => x.InitialOrderId).IsUnique();
        builder.ReferencesAccount(nameof(TraceabilityLot.DisposedByAccountId));

        builder.Property(x => x.DisposalMethod).HasMaxLength(Lengths.Name);
        builder.Property(x => x.DisposalReason).HasMaxLength(Lengths.Reason);
    }
}

internal sealed class TracePublicationVersionConfiguration : IEntityTypeConfiguration<TracePublicationVersion>
{
    public void Configure(EntityTypeBuilder<TracePublicationVersion> builder)
    {
        builder.HasOne(x => x.Lot).WithMany(l => l.PublicationVersions).HasForeignKey(x => x.LotId);
        builder.HasIndex(x => new { x.LotId, x.VersionNumber }).IsUnique();
        builder.ReferencesAccount(
            nameof(TracePublicationVersion.ReviewedByAccountId),
            nameof(TracePublicationVersion.PublishedByAccountId),
            nameof(TracePublicationVersion.WithdrawnByAccountId));

        builder.Property(x => x.ReviewErrors).HasMaxLength(Lengths.LongText);
        builder.Property(x => x.WithdrawalReason).HasMaxLength(Lengths.Reason);
    }
}

internal sealed class TraceCorrectionConfiguration : IEntityTypeConfiguration<TraceCorrection>
{
    public void Configure(EntityTypeBuilder<TraceCorrection> builder)
    {
        builder.HasOne(x => x.Lot).WithMany(l => l.Corrections).HasForeignKey(x => x.LotId);
        builder.ReferencesAccount(nameof(TraceCorrection.CorrectedByAccountId), nameof(TraceCorrection.ApprovedByAccountId));
        builder.Property(x => x.Field).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.Reason).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.PublicationImpact).HasMaxLength(Lengths.Address);
    }
}
