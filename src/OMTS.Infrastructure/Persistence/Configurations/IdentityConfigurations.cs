using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities;

namespace OMTS.Infrastructure.Persistence.Configurations;

internal sealed class CooperativeConfiguration : IEntityTypeConfiguration<Cooperative>
{
    public void Configure(EntityTypeBuilder<Cooperative> builder)
    {
        builder.Property(x => x.FullName).HasMaxLength(Lengths.Name);
        builder.Property(x => x.TaxCode).HasMaxLength(Lengths.Phone);
        builder.HasIndex(x => x.TaxCode).IsUnique();
        builder.Property(x => x.OfficeAddress).HasMaxLength(Lengths.Address);
        builder.Property(x => x.PhoneNumber).HasMaxLength(Lengths.Phone);
        builder.Property(x => x.Email).HasMaxLength(Lengths.Email);
        builder.Property(x => x.Website).HasMaxLength(Lengths.Url);
        builder.Property(x => x.LegalRepresentative).HasMaxLength(Lengths.Name);
        builder.Property(x => x.BusinessLines).HasMaxLength(Lengths.LongText);

        builder.OwnsOne(x => x.BankAccount, bank =>
        {
            bank.Property(b => b.BankName).HasMaxLength(Lengths.Name);
            bank.Property(b => b.AccountNumber).HasMaxLength(Lengths.Code);
            bank.Property(b => b.AccountHolderName).HasMaxLength(Lengths.Name);
        });
    }
}

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
            preferences.Property(p => p.Category).HasMaxLength(Lengths.ShortText);
        });
    }
}

internal sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> builder)
    {
        builder.Property(x => x.FullName).HasMaxLength(Lengths.Name);
        builder.Property(x => x.PhoneNumber).HasMaxLength(Lengths.Phone);
        builder.Property(x => x.Address).HasMaxLength(Lengths.Address);
        builder.Property(x => x.Area).HasMaxLength(Lengths.Name);
        builder.Property(x => x.Ward).HasMaxLength(Lengths.Name);
        builder.Property(x => x.Notes).HasMaxLength(Lengths.LongText);
        builder.Property(x => x.OtherInformation).HasMaxLength(Lengths.LongText);

        builder.HasOne(x => x.Cooperative).WithMany(c => c.Employees).HasForeignKey(x => x.CooperativeId);
        builder.HasOne(x => x.Account).WithOne(a => a.Employee).HasForeignKey<Employee>(x => x.AccountId);
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.Property(x => x.Name).HasMaxLength(Lengths.ShortText);
        builder.HasIndex(x => x.Name).IsUnique();
        builder.Property(x => x.Description).HasMaxLength(Lengths.Reason);
    }
}

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.HasKey(x => new { x.RoleId, x.FunctionCode });
        builder.Property(x => x.FunctionCode).HasMaxLength(Lengths.ShortText);
        builder.HasOne(x => x.Role).WithMany(r => r.Permissions).HasForeignKey(x => x.RoleId);
    }
}

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

internal sealed class VerificationCodeConfiguration : IEntityTypeConfiguration<VerificationCode>
{
    public void Configure(EntityTypeBuilder<VerificationCode> builder)
    {
        builder.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId);
        builder.Property(x => x.Target).HasMaxLength(Lengths.Email);
        builder.Property(x => x.CodeHash).HasMaxLength(Lengths.Address);
        builder.HasIndex(x => new { x.Target, x.Purpose, x.CreatedAt });
    }
}
