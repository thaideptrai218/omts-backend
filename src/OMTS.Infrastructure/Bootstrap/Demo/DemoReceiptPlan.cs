using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <param name="Code">Business code of the demo receipt, kept at the start of its notes: the service generates the receipt's own code from today's date, so this is what makes the seed repeatable.</param>
/// <param name="RaftNumber">Raft of the receipt's zone (1…10); needed when a line is raft material.</param>
internal sealed record DemoReceiptPlan(
    string Code,
    string SupplierPhone,
    string Zone,
    int? RaftNumber,
    int DaysAgo,
    DemoReceiptOutcome Outcome,
    string Note,
    DemoReceiptLine[] Lines,
    decimal Discount = 0,
    decimal PaidNow = 0,
    PaymentMethod PaidNowMethod = PaymentMethod.Cash,
    DemoLaterPayment[]? Later = null,
    DemoReturn? Return = null,
    string? CancelReason = null);
