using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Configurations.Identity;

internal sealed class VerificationCodeConfiguration : IEntityTypeConfiguration<VerificationCode>
{
    public void Configure(EntityTypeBuilder<VerificationCode> builder)
    {
        builder.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId);
        builder.Property(x => x.Target).HasMaxLength(Lengths.Email);
        builder.Property(x => x.CodeHash).HasMaxLength(Lengths.Hash);
        builder.Property(x => x.TokenHash).HasMaxLength(Lengths.Hash);
        builder.HasIndex(x => x.TokenHash).IsUnique();
        builder.HasIndex(x => new { x.Target, x.Purpose, x.CreatedAt });
        builder.HasIndex(x => new { x.AccountId, x.Purpose });
        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}
