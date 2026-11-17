using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class EmployeeSessionConfiguration : IEntityTypeConfiguration<EmployeeSession>
{
    public void Configure(EntityTypeBuilder<EmployeeSession> builder)
    {
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        builder.HasOne(x => x.EmployeeDevice).WithMany().HasForeignKey(x => x.EmployeeDeviceId);
        // Concurrency token: of two refreshes racing with the same token only one can rotate it; the other is answered
        // from the winner's rotation (the replaced token's grace period), as for a staff session.
        builder.Property(x => x.RefreshTokenHash).HasMaxLength(Lengths.Hash).IsConcurrencyToken();
        builder.HasIndex(x => x.RefreshTokenHash).IsUnique();
        builder.Property(x => x.PreviousRefreshTokenHash).HasMaxLength(Lengths.Hash);
        builder.HasIndex(x => x.PreviousRefreshTokenHash).HasFilter("[PreviousRefreshTokenHash] IS NOT NULL");
        builder.Property(x => x.RevokeReason).HasMaxLength(Lengths.Name);
        builder.HasIndex(x => new { x.EmployeeId, x.RevokedAt });
    }
}
