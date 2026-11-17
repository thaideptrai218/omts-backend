using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class EmployeeDeviceConfiguration : IEntityTypeConfiguration<EmployeeDevice>
{
    public void Configure(EntityTypeBuilder<EmployeeDevice> builder)
    {
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        builder.HasOne(x => x.LinkedByLoginCode).WithMany().HasForeignKey(x => x.LinkedByLoginCodeId);
        builder.Property(x => x.DeviceSecretHash).HasMaxLength(Lengths.Hash);
        builder.Property(x => x.PreviousDeviceSecretHash).HasMaxLength(Lengths.Hash);
        builder.Property(x => x.Platform).HasMaxLength(Lengths.Code);
        builder.Property(x => x.DeviceName).HasMaxLength(Lengths.Name);
        builder.Property(x => x.PushToken).HasMaxLength(500);
        builder.Property(x => x.RowVersion).IsRowVersion();
        // An employee has one linked device (BR-LABOR-02): parallel links cannot both succeed.
        builder.HasIndex(x => x.EmployeeId)
            .IsUnique()
            .HasDatabaseName(EmployeeAppConfigurations.OneLinkedDevicePerEmployee)
            .HasFilter("[UnlinkedAt] IS NULL");
    }
}
