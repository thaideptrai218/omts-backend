using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities;

namespace OMTS.Infrastructure.Persistence.Configurations;

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

internal sealed class ReceiptAllocationConfiguration : IEntityTypeConfiguration<ReceiptAllocation>
{
    public void Configure(EntityTypeBuilder<ReceiptAllocation> builder)
    {
        builder.HasOne(x => x.Receipt).WithMany(r => r.Allocations).HasForeignKey(x => x.ReceiptId);
        builder.HasOne(x => x.Sale).WithMany(s => s.ReceiptAllocations).HasForeignKey(x => x.SaleId);
        builder.ToTable(t => t.HasCheckConstraint("CK_ReceiptAllocation_Amount_Positive", "[AllocatedAmount] > 0"));
        builder.HasThousandVndCheck(nameof(ReceiptAllocation.AllocatedAmount));
    }
}

internal sealed class CustomerCreditConfiguration : IEntityTypeConfiguration<CustomerCredit>
{
    public void Configure(EntityTypeBuilder<CustomerCredit> builder)
    {
        builder.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
        builder.HasOne(x => x.SourceSale).WithMany().HasForeignKey(x => x.SourceSaleId);
        builder.HasOne(x => x.SourceAdjustment).WithMany().HasForeignKey(x => x.SourceAdjustmentId);
        builder.HasOne(x => x.SourceReceipt).WithMany().HasForeignKey(x => x.SourceReceiptId);
        builder.HasThousandVndCheck(nameof(CustomerCredit.Amount));
    }
}

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
