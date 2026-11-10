namespace OMTS.Domain.Enums;

/// <summary>The pot a cash book line belongs to: its balance is kept apart from the others.</summary>
public enum CashFund
{
    Cash,
    Bank,
    EWallet,
}

/// <summary>Money in (a receipt voucher) or money out (a payment voucher).</summary>
public enum CashDirection
{
    Receipt,
    Payment,
}

/// <summary>
/// What a voucher type is for. Goods are the supplier payments and money paid back by a supplier, payroll the wages; those
/// two are written by their own modules, so only the other categories may be chosen on a self-made voucher.
/// </summary>
public enum CashCategory
{
    Goods,
    OtherIncome,
    TransferWithdrawal,
    OtherExpense,
    Payroll,
}

/// <summary>Who paid or received the money of a voucher.</summary>
public enum CashPartyKind
{
    Customer,
    Supplier,
    Employee,
    Other,
}

/// <summary>A cash account other than the cash drawer: a bank account or an e-wallet.</summary>
public enum CashAccountKind
{
    Bank,
    EWallet,
}
