using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Configurations.Identity;

internal sealed class AccessGrantConfiguration : IEntityTypeConfiguration<AccessGrant>
{
    public void Configure(EntityTypeBuilder<AccessGrant> builder)
    {
        builder.HasOne(x => x.Account).WithMany(a => a.AccessGrants).HasForeignKey(x => x.AccountId);
        builder.HasOne(x => x.Role).WithMany(r => r.AccessGrants).HasForeignKey(x => x.RoleId);
        builder.HasOne(x => x.Zone).WithMany().HasForeignKey(x => x.ZoneId);
        builder.ReferencesAccount(nameof(AccessGrant.GrantedByAccountId), nameof(AccessGrant.RevokedByAccountId));
        builder.Property(x => x.RevokeReason).HasMaxLength(Lengths.Reason);
        builder.HasIndex(x => new { x.AccountId, x.StatusId });
    }
}
