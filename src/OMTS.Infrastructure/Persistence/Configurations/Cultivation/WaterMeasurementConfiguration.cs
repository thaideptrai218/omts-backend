using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Application.Features.WaterQuality;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Infrastructure.Persistence.Configurations.Cultivation;

internal sealed class WaterMeasurementConfiguration : IEntityTypeConfiguration<WaterMeasurement>
{
    public void Configure(EntityTypeBuilder<WaterMeasurement> builder)
    {
        builder.HasOne(x => x.Zone).WithMany(z => z.WaterMeasurements).HasForeignKey(x => x.ZoneId);
        builder.HasOne(x => x.Raft).WithMany().HasForeignKey(x => x.RaftId);
        builder.ReferencesAccount(nameof(WaterMeasurement.MeasuredByAccountId));

        builder.Property(x => x.PhotoUrl).HasMaxLength(WaterMeterPhotoStore.MaxKeyLength);
        builder.Property(x => x.ViolatedValues).HasMaxLength(Lengths.Name);
        builder.Property(x => x.VoidReason).HasMaxLength(Lengths.Reason);
        foreach (var fourDigits in new[]
                 {
                     nameof(WaterMeasurement.Ph), nameof(WaterMeasurement.PhMinUsed), nameof(WaterMeasurement.PhMaxUsed),
                     nameof(WaterMeasurement.Temperature), nameof(WaterMeasurement.TemperatureMinUsed), nameof(WaterMeasurement.TemperatureMaxUsed),
                 })
        {
            builder.Property(fourDigits).HasPrecision(4, 2);
        }

        foreach (var salinity in new[] { nameof(WaterMeasurement.Salinity), nameof(WaterMeasurement.SalinityMinUsed), nameof(WaterMeasurement.SalinityMaxUsed) })
        {
            builder.Property(salinity).HasPrecision(5, 2);
        }

        builder.HasIndex(x => new { x.ZoneId, x.MeasuredAt });
        // Re-sends: one record per client operation of an account.
        builder.HasIndex(x => new { x.MeasuredByAccountId, x.ClientOperationId }).IsUnique();
        // Each measurement has a meter photo of its own (BR-WATER-02).
        builder.HasIndex(x => x.PhotoUrl).IsUnique();
    }
}
