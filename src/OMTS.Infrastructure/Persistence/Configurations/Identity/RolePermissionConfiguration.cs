using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Configurations.Identity;

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.HasKey(x => new { x.RoleId, x.FunctionCode });
        builder.Property(x => x.FunctionCode).HasMaxLength(Lengths.ShortText);
        builder.HasOne(x => x.Role).WithMany(r => r.Permissions).HasForeignKey(x => x.RoleId);
    }
}
