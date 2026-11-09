using OMTS.Application.Features.Traceability.Tracing;
using OMTS.Application.Features.Traceability.Withdrawal;

namespace OMTS.Application.UnitTests.Features.Traceability;

/// <summary>BR-TRACE-07: the traders a withdrawal tells, from the forward trace: each once with all their invoices, walk-in invoices apart, nobody when no invoice sells the lot.</summary>
public sealed class WithdrawalAudienceTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 8, 2, 0, 0, TimeSpan.Zero);
    private static readonly Guid One = Guid.NewGuid();
    private static readonly Guid Two = Guid.NewGuid();
    private static readonly Guid Account = Guid.NewGuid();

    private static LotSaleLine Line(string invoice, int number, Guid? customer, string? name = null, string? phone = null, Guid? account = null) =>
        new(Guid.NewGuid(), invoice, At, new DateOnly(2026, 11, 8), Guid.NewGuid(), "KV01", customer, name, phone, number, "Oysters", 1m, 30_000m, 30_000m, account);

    [Fact]
    public void A_trader_on_several_invoices_is_told_once_with_every_invoice_and_walk_ins_are_listed_apart()
    {
        var audience = WithdrawalAudience.Of(
        [
            Line("HD000001", 1, One, "DEMO trader one", "999900000001", Account),
            Line("HD000001", 2, One, "DEMO trader one", "999900000001", Account),
            Line("HD000002", 1, null),
            Line("HD000003", 1, Two, "DEMO trader two"),
            Line("HD000004", 1, One, "DEMO trader one", "999900000001", Account),
            Line("HD000005", 1, null),
        ]);

        Assert.Equal(
            new (Guid, string, string?, Guid?, string)[] { (One, "DEMO trader one", "999900000001", Account, "HD000001, HD000004"), (Two, "DEMO trader two", null, null, "HD000003") },
            audience.Traders.Select(t => (t.CustomerId, t.CustomerName, t.PhoneNumber, t.TraderAccountId, string.Join(", ", t.InvoiceCodes))));
        Assert.Equal(["HD000002", "HD000005"], audience.WalkInInvoiceCodes);
        Assert.False(audience.NoInvoice);
    }

    [Fact]
    public void A_lot_on_walk_in_invoices_only_tells_nobody_but_is_still_sold_and_no_invoice_means_msg_trace_06()
    {
        var walkInsOnly = WithdrawalAudience.Of([Line("HD000002", 1, null)]);
        var none = WithdrawalAudience.Of([]);

        Assert.Empty(walkInsOnly.Traders);
        Assert.False(walkInsOnly.NoInvoice);
        Assert.True(none.NoInvoice);
    }
}
