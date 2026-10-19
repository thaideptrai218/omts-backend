using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Finance;

namespace OMTS.Infrastructure.Persistence.Configurations.Finance;

internal sealed class CustomerRefundConfiguration : IEntityTypeConfiguration<CustomerRefund>
{
    public void Configure(EntityTypeBuilder<CustomerRefund> builder)
    {
        builder.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
        builder.HasOne(x => x.Credit).WithMany(c => c.Refunds).HasForeignKey(x => x.CreditId);
        builder.Property(x => x.Recipient).HasMaxLength(Lengths.Name);
        builder.Property(x => x.TransferReference).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.DeduplicationKey).HasMaxLength(Lengths.ShortText);
        builder.HasIndex(x => x.DeduplicationKey).IsUnique();
        builder.ToTable(t => t.HasCheckConstraint("CK_CustomerRefund_Amount_Positive", "[AmountPaid] > 0"));
        builder.HasThousandVndCheck(nameof(CustomerRefund.AmountPaid));
    }
}
