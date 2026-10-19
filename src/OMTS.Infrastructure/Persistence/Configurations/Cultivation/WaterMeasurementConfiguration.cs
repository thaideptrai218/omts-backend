using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Infrastructure.Persistence.Configurations.Cultivation;

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
