using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Configurations.Identity;

internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.Property(x => x.DisplayName).HasMaxLength(Lengths.Name);
        builder.Property(x => x.Username).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Username).IsUnique();
        builder.Property(x => x.PhoneNumber).HasMaxLength(Lengths.Phone);
        builder.HasIndex(x => x.PhoneNumber).IsUnique();
        builder.Property(x => x.Email).HasMaxLength(Lengths.Email);
        // Only a verified email is unique system-wide (BR-32).
        builder.HasIndex(x => x.Email).IsUnique().HasFilter("[Email] IS NOT NULL AND [EmailVerifiedAt] IS NOT NULL");
        builder.Property(x => x.PasswordHash).HasMaxLength(Lengths.Address);

        builder.OwnsMany(x => x.NotificationPreferences, preferences =>
        {
            preferences.ToJson();
            preferences.Property(p => p.Type).HasMaxLength(Lengths.ShortText);
        });
    }
}
