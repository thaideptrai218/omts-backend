using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Configurations.Identity;

internal sealed class PushDeviceConfiguration : IEntityTypeConfiguration<PushDevice>
{
    public void Configure(EntityTypeBuilder<PushDevice> builder)
    {
        builder.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId);
        builder.Property(x => x.Token).HasMaxLength(Lengths.Address);
        // One device token belongs to one account: re-registering on another account moves it.
        builder.HasIndex(x => x.Token).IsUnique();
        builder.Property(x => x.Platform).HasMaxLength(20);
    }
}
