using OMTS.Domain.Common;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.CashBook;

/// <summary>
/// A bank account or an e-wallet of the cooperative that money is paid into or out of, other than the cash drawer. Cooperative-wide,
/// kept by the Accountant. One of them may be the account of the VietQR codes: it is the one that matches the cooperative's
/// receiving bank account, never stored as a flag. An account used by a voucher or a payment is deactivated, not deleted.
/// </summary>
public class CashAccount : AuditableEntity
{
    public CashAccountKind Kind { get; set; }

    /// <summary>The NAPAS bank identification number of a Bank account; empty for an e-wallet.</summary>
    public string? BankBin { get; set; }

    /// <summary>The short name of the bank (from the bank list at the time of the save), or the wallet provider (MoMo, ZaloPay…).</summary>
    public string ProviderName { get; set; } = string.Empty;

    public string AccountNumber { get; set; } = string.Empty;
    public string HolderName { get; set; } = string.Empty;
    public string? Note { get; set; }

    /// <summary>False once the account is no longer used: kept on old vouchers, not offered on new ones.</summary>
    public bool IsActive { get; set; } = true;
}
