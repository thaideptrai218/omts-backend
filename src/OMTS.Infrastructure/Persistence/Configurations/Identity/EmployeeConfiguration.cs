using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Constants;
using OMTS.Application.Features.Employees;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Configurations.Identity;

internal sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> builder)
    {
        builder.Property(x => x.FullName).HasMaxLength(Lengths.Name);
        builder.Property(x => x.PhoneNumber).HasMaxLength(Lengths.Phone);
        // No other Employed employee has the number (BR-LABOR-01): parallel saves cannot both pass the pre-check. Left
        // employees keep theirs, and any number of them may share it.
        builder.HasIndex(x => x.PhoneNumber)
            .IsUnique()
            .HasDatabaseName("UX_Employees_PhoneNumber_Employed")
            .HasFilter($"[PhoneNumber] IS NOT NULL AND [StatusId] = {StatusIds.Employee.Working}");
        builder.Property(x => x.Code).HasMaxLength(EmployeeCodes.MaxLength);
        // The code is unique within the cooperative, whatever its letter case (the database collation is case-insensitive). Rows
        // older than the column have none until the migration fills them, hence the filter.
        builder.HasIndex(x => new { x.CooperativeId, x.Code })
            .IsUnique()
            .HasDatabaseName("UX_Employees_Cooperative_Code")
            .HasFilter("[Code] IS NOT NULL");
        builder.Property(x => x.IdNumber).HasMaxLength(12);
        builder.Property(x => x.Email).HasMaxLength(254);
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
