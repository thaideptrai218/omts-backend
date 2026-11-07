using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Constants;
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
        // FARM-05.0.E1 under a race: one Effective pair per account, role and zone. SQL Server compares NULL zones as
        // equal in a unique index, so this also covers All zones. A one-zone pair racing an All-zones pair of the same
        // role is not covered here; that race is accepted (the pre-check of AccessGrantService catches every other case).
        builder.HasIndex(x => new { x.AccountId, x.RoleId, x.ZoneId })
            .IsUnique()
            .HasFilter($"[StatusId] = {StatusIds.AccessGrant.Effective}")
            .HasDatabaseName("UX_AccessGrants_EffectivePair");
    }
}
