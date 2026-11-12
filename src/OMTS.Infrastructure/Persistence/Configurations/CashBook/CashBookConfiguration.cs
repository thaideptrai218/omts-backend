using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.CashBook;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Configurations.CashBook;

internal sealed class CashAccountConfiguration : IEntityTypeConfiguration<CashAccount>
{
    public void Configure(EntityTypeBuilder<CashAccount> builder)
    {
        builder.Property(x => x.BankBin).HasMaxLength(Lengths.Phone);
        builder.Property(x => x.ProviderName).HasMaxLength(Lengths.Name);
        builder.Property(x => x.AccountNumber).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.HolderName).HasMaxLength(Lengths.Name);
        builder.Property(x => x.Note).HasMaxLength(Lengths.Reason);
        // One account per bank or wallet and number; SQL Server compares these case-insensitively.
        builder.HasIndex(x => new { x.Kind, x.ProviderName, x.AccountNumber }).IsUnique();
    }
}

internal sealed class CashVoucherTypeConfiguration : IEntityTypeConfiguration<CashVoucherType>
{
    /// <summary>The seeded types carry this creation time; they are made by the system, not by a person.</summary>
    private static readonly DateTimeOffset SeededAt = new(2026, 11, 10, 0, 0, 0, TimeSpan.FromHours(7));

    public void Configure(EntityTypeBuilder<CashVoucherType> builder)
    {
        builder.Property(x => x.Name).HasMaxLength(Lengths.Name);
        builder.Property(x => x.Description).HasMaxLength(Lengths.Reason);
        builder.HasIndex(x => new { x.Direction, x.IsActive });
        builder.HasData(
            Seed(CashVoucherTypeIds.ReceiptFromCustomer, CashDirection.Receipt, CashCategory.Goods, "Thu tiền khách trả", manual: false),
            Seed(CashVoucherTypeIds.ReceiptSupplierPaidBack, CashDirection.Receipt, CashCategory.Goods, "Thu tiền NCC hoàn trả", manual: false),
            Seed(CashVoucherTypeIds.ReceiptOther, CashDirection.Receipt, CashCategory.OtherIncome, "Thu khác", manual: true),
            Seed(CashVoucherTypeIds.ReceiptTransferWithdrawal, CashDirection.Receipt, CashCategory.TransferWithdrawal, "Chuyển/Rút", manual: true),
            Seed(CashVoucherTypeIds.PaymentToSupplier, CashDirection.Payment, CashCategory.Goods, "Chi tiền trả NCC", manual: false),
            Seed(CashVoucherTypeIds.PaymentPayroll, CashDirection.Payment, CashCategory.Payroll, "Chi trả lương NV", manual: false),
            Seed(CashVoucherTypeIds.PaymentOther, CashDirection.Payment, CashCategory.OtherExpense, "Chi phí khác", manual: true),
            Seed(CashVoucherTypeIds.PaymentToCustomer, CashDirection.Payment, CashCategory.Goods, "Chi trả lại khách", manual: false));
    }

    // Anonymous objects: the key has no public setter. Other income, other expense and wages count in the business result; goods and
    // transfers between funds do not.
    private static object Seed(Guid id, CashDirection direction, CashCategory category, string name, bool manual) => new
    {
        Id = id,
        Direction = direction,
        Category = category,
        Name = name,
        CountsInResult = category is CashCategory.OtherIncome or CashCategory.OtherExpense or CashCategory.Payroll,
        IsSystem = true,
        Manual = manual,
        IsActive = true,
        CreatedAt = SeededAt,
        // The in-memory test provider needs a value for the row version; SQL Server generates its own and takes none.
        RowVersion = Array.Empty<byte>(),
    };
}

internal sealed class CashPartyConfiguration : IEntityTypeConfiguration<CashParty>
{
    public void Configure(EntityTypeBuilder<CashParty> builder)
    {
        builder.Property(x => x.Name).HasMaxLength(Lengths.Name);
        builder.Property(x => x.PhoneNumber).HasMaxLength(Lengths.Phone);
        builder.Property(x => x.Address).HasMaxLength(Lengths.Address);
        builder.Property(x => x.Area).HasMaxLength(Lengths.Name);
        builder.Property(x => x.Ward).HasMaxLength(Lengths.Name);
        builder.Property(x => x.Note).HasMaxLength(Lengths.Reason);
    }
}

internal sealed class CashVoucherConfiguration : IEntityTypeConfiguration<CashVoucher>
{
    public void Configure(EntityTypeBuilder<CashVoucher> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasOne(x => x.CashAccount).WithMany().HasForeignKey(x => x.CashAccountId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Type).WithMany().HasForeignKey(x => x.TypeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OMTS.Domain.Entities.Identity.Account>().WithMany().HasForeignKey(x => x.CollectedByAccountId);
        // Whole VND, above zero (checked by the service); the column holds the same range as a supplier payment.
        builder.Property(x => x.Amount).HasPrecision(18, 0);
        builder.Property(x => x.PartyName).HasMaxLength(Lengths.Name);
        builder.Property(x => x.Note).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.CancelReason).HasMaxLength(Lengths.Reason);
        // The book reads one fund over a period.
        builder.HasIndex(x => new { x.Fund, x.VoucherDate });
    }
}
