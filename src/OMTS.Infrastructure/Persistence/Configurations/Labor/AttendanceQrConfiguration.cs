using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;
namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class AttendanceQrConfiguration : IEntityTypeConfiguration<AttendanceQr>
{
    public void Configure(EntityTypeBuilder<AttendanceQr> builder)
    {
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        builder.Property(x => x.TokenHash).HasMaxLength(64);
        builder.HasIndex(x => x.TokenHash).IsUnique();
        builder.HasIndex(x => x.EmployeeId).IsUnique();
        builder.HasIndex(x => x.ExpiresAt);
    }
}
