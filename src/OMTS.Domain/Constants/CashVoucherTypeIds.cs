namespace OMTS.Domain.Constants;

/// <summary>
/// Fixed primary keys of the seeded system voucher types, so code can point at them (the supplier payments in the cash book
/// read as the goods types) and every environment holds the same rows.
/// </summary>
public static class CashVoucherTypeIds
{
    public static readonly Guid ReceiptFromCustomer = new("5d6c1a10-0000-4c3a-9a10-000000000001");
    public static readonly Guid ReceiptSupplierPaidBack = new("5d6c1a10-0000-4c3a-9a10-000000000002");
    public static readonly Guid ReceiptOther = new("5d6c1a10-0000-4c3a-9a10-000000000003");
    public static readonly Guid ReceiptTransferWithdrawal = new("5d6c1a10-0000-4c3a-9a10-000000000004");
    public static readonly Guid PaymentToSupplier = new("5d6c1a10-0000-4c3a-9a10-000000000005");
    public static readonly Guid PaymentPayroll = new("5d6c1a10-0000-4c3a-9a10-000000000006");
    public static readonly Guid PaymentOther = new("5d6c1a10-0000-4c3a-9a10-000000000007");
    public static readonly Guid PaymentToCustomer = new("5d6c1a10-0000-4c3a-9a10-000000000008");

    /// <summary>The pay of people hired outside the system for the day of a work-day plan; made by the plan, not by hand.</summary>
    public static readonly Guid PaymentOutsiderLabour = new("5d6c1a10-0000-4c3a-9a10-000000000009");
}
