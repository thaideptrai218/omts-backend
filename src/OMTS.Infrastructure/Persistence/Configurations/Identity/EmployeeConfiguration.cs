using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Configurations.Identity;

internal sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> builder)
    {
        builder.Property(x => x.FullName).HasMaxLength(Lengths.Name);
        builder.Property(x => x.PhoneNumber).HasMaxLength(Lengths.Phone);
        // One profile per person (LAB-01): parallel saves of the same person cannot both pass the pre-check.
        builder.HasIndex(x => new { x.FullName, x.PhoneNumber }).IsUnique().HasFilter("[PhoneNumber] IS NOT NULL");
        builder.Property(x => x.Address).HasMaxLength(Lengths.Address);
        builder.Property(x => x.Area).HasMaxLength(Lengths.Name);
        builder.Property(x => x.Ward).HasMaxLength(Lengths.Name);
        builder.Property(x => x.Notes).HasMaxLength(Lengths.LongText);
        builder.Property(x => x.OtherInformation).HasMaxLength(Lengths.LongText);

        builder.HasOne(x => x.Cooperative).WithMany(c => c.Employees).HasForeignKey(x => x.CooperativeId);
        builder.HasOne(x => x.Account).WithOne(a => a.Employee).HasForeignKey<Employee>(x => x.AccountId);

        builder.OwnsOne(x => x.BankAccount, bank =>
        {
            bank.Property(b => b.BankName).HasMaxLength(Lengths.ShortText);
            bank.Property(b => b.AccountNumber).HasMaxLength(Lengths.Phone);
            bank.Property(b => b.AccountHolderName).HasMaxLength(Lengths.ShortText);
        });
    }
}
