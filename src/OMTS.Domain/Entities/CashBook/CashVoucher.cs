using OMTS.Domain.Common;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.CashBook;

/// <summary>
/// A self-made receipt or payment voucher of the cash book (money that is not a supplier payment, whose own record shows in the
/// book too). Everything on it may be edited while it is not cancelled; one made by mistake is cancelled with an optional reason
/// and no longer counts. Every change is audited.
/// </summary>
public class CashVoucher : AuditableEntity
{
    /// <summary>TTM/CTM (Cash), TNH/CNH (Bank), TVD/CVD (EWallet) and six digits, or a code the user typed; unique regardless of case.</summary>
    public string Code { get; set; } = string.Empty;

    public CashDirection Direction { get; set; }
    public CashFund Fund { get; set; }

    /// <summary>The bank account or e-wallet of a Bank or EWallet voucher; none on a Cash one.</summary>
    public Guid? CashAccountId { get; set; }
    public CashAccount? CashAccount { get; set; }

    public Guid TypeId { get; set; }
    public CashVoucherType Type { get; set; } = null!;

    /// <summary>When the money moved, stored at UTC+7, never later than now.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>The business date (UTC+7) of <see cref="OccurredAt"/>; the balances and every period filter read it.</summary>
    public DateOnly VoucherDate { get; set; }

    /// <summary>Whole VND above zero; the direction gives the sign.</summary>
    public decimal Amount { get; set; }

    /// <summary>The Owner or staff member who collected or paid the money.</summary>
    public Guid? CollectedByAccountId { get; set; }

    public CashPartyKind? PartyKind { get; set; }

    /// <summary>The customer, supplier, employee or <see cref="CashParty"/> named; no foreign key, as the kind decides the table.</summary>
    public Guid? PartyId { get; set; }

    /// <summary>The name of the party when the voucher was saved, so the voucher keeps reading the same if the party is renamed.</summary>
    public string? PartyName { get; set; }

    public string? Note { get; set; }
    public bool CountsInResult { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
}
