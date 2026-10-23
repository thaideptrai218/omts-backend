using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Finance;

namespace OMTS.Infrastructure.Persistence.Configurations.Finance;

internal sealed class CustomerReceiptConfiguration : IEntityTypeConfiguration<CustomerReceipt>
{
    public void Configure(EntityTypeBuilder<CustomerReceipt> builder)
    {
        builder.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
        builder.Property(x => x.ReferenceCode).HasMaxLength(Lengths.ShortText);
        // UC49 checks duplicates on the reference code per method.
        builder.HasIndex(x => new { x.Method, x.ReferenceCode }).IsUnique().HasFilter("[ReferenceCode] IS NOT NULL");
        builder.Property(x => x.DeduplicationKey).HasMaxLength(Lengths.ShortText);
        builder.HasIndex(x => x.DeduplicationKey).IsUnique();
        builder.ToTable(t => t.HasCheckConstraint("CK_CustomerReceipt_Amount_Positive", "[Amount] > 0"));
        builder.HasThousandVndCheck(nameof(CustomerReceipt.Amount));
    }
}
