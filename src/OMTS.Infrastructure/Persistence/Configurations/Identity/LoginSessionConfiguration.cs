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
        // fails and the session is revoked as reuse.
        builder.Property(x => x.RefreshTokenHash).HasMaxLength(Lengths.Hash).IsConcurrencyToken();
        builder.HasIndex(x => x.RefreshTokenHash).IsUnique();
        builder.Property(x => x.RevokeReason).HasMaxLength(Lengths.Name);
        builder.Property(x => x.DeviceName).HasMaxLength(Lengths.Name);
        builder.Property(x => x.UserAgent).HasMaxLength(Lengths.Address);
        builder.Property(x => x.IpAddress).HasMaxLength(64);
    }
}
