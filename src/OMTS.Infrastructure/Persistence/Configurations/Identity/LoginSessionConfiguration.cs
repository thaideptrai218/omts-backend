using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Configurations.Identity;

internal sealed class LoginSessionConfiguration : IEntityTypeConfiguration<LoginSession>
{
    public void Configure(EntityTypeBuilder<LoginSession> builder)
    {
        builder.HasOne(x => x.Account).WithMany(a => a.LoginSessions).HasForeignKey(x => x.AccountId);
        // Concurrency token: of two refreshes racing with the same token only one can rotate it; the other
        // fails and is answered from the winner's rotation (the replaced token's grace period).
        builder.Property(x => x.RefreshTokenHash).HasMaxLength(Lengths.Hash).IsConcurrencyToken();
        builder.HasIndex(x => x.RefreshTokenHash).IsUnique();
        builder.Property(x => x.PreviousRefreshTokenHash).HasMaxLength(Lengths.Hash);
        builder.HasIndex(x => x.PreviousRefreshTokenHash).HasFilter("[PreviousRefreshTokenHash] IS NOT NULL");
        // The revocation sweeper reads the sessions revoked in the last minutes.
        builder.HasIndex(x => x.RevokedAt);
        builder.Property(x => x.RevokeReason).HasMaxLength(Lengths.Name);
        // Base64url of a 256-bit keyed hash: 43 characters. The device list and the signed-out-device check look sessions up by it.
        builder.Property(x => x.DeviceKey).HasMaxLength(64);
        builder.HasIndex(x => new { x.AccountId, x.DeviceKey });
        builder.Property(x => x.DeviceName).HasMaxLength(Lengths.Name);
        builder.Property(x => x.UserAgent).HasMaxLength(Lengths.Address);
        builder.Property(x => x.IpAddress).HasMaxLength(64);
    }
}
