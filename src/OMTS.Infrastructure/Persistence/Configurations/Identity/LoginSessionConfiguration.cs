using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Configurations.Identity;

internal sealed class LoginSessionConfiguration : IEntityTypeConfiguration<LoginSession>
{
    public void Configure(EntityTypeBuilder<LoginSession> builder)
    {
        builder.HasOne(x => x.Account).WithMany(a => a.LoginSessions).HasForeignKey(x => x.AccountId);
        builder.Property(x => x.RefreshTokenHash).HasMaxLength(Lengths.Hash);
        builder.HasIndex(x => x.RefreshTokenHash).IsUnique();
        builder.Property(x => x.PreviousRefreshTokenHash).HasMaxLength(Lengths.Hash);
        builder.HasIndex(x => x.PreviousRefreshTokenHash);
        builder.Property(x => x.RevokeReason).HasMaxLength(Lengths.Name);
        builder.Property(x => x.DeviceName).HasMaxLength(Lengths.Name);
        builder.Property(x => x.UserAgent).HasMaxLength(Lengths.Address);
        builder.Property(x => x.IpAddress).HasMaxLength(64);
    }
}
