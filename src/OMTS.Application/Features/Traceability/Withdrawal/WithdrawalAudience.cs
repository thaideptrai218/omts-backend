using OMTS.Application.Features.Traceability.Tracing;

namespace OMTS.Application.Features.Traceability.Withdrawal;

/// <summary>One trader to notify of a withdrawal (BR-TRACE-07): a customer profile on at least one current sales invoice of the lot, with all those invoices, the oldest first.</summary>
/// <param name="PhoneNumber">Null when the profile has none: nothing can be sent and the Cooperative Owner is told to call (TRACE-04 step 7).</param>
/// <param name="TraderAccountId">The trader account the profile is linked to: the notice is also in its list and pushed (TRACE-04.1); null without one.</param>
public sealed record WithdrawalTrader(Guid CustomerId, string CustomerName, string? PhoneNumber, Guid? TraderAccountId, IReadOnlyList<string> InvoiceCodes);

/// <summary>
/// Who a withdrawal tells (TRACE-04 step 1, which includes Trace lot): the traders of the forward trace (BR-TRACE-03), each once whatever the number of their invoices, and the walk-in
/// invoices, which name nobody to notify, listed for the Cooperative Owner.
/// </summary>
public sealed record WithdrawalAudience(IReadOnlyList<WithdrawalTrader> Traders, IReadOnlyList<string> WalkInInvoiceCodes)
{
    /// <summary>TRACE-04.2: no current sales invoice sells the lot (MSG-TRACE-06).</summary>
    public bool NoInvoice => Traders.Count == 0 && WalkInInvoiceCodes.Count == 0;

    /// <param name="lines">The forward trace: the lines of the lot on invoices that are not cancelled, the oldest invoice first.</param>
    public static WithdrawalAudience Of(IReadOnlyList<LotSaleLine> lines)
    {
        // An invoice that a sales return took back in full (a return always takes back the whole invoice) tells no trader (BR-TRACE-07).
        lines = lines.Where(l => !l.TakenBackByReturn).ToList();
        var traders = lines
            .Where(l => l.CustomerId is not null)
            .GroupBy(l => l.CustomerId!.Value)
            .Select(g => new WithdrawalTrader(
                g.Key, g.First().CustomerName ?? string.Empty, g.First().CustomerPhone, g.First().CustomerAccountId, g.Select(l => l.InvoiceCode).Distinct().ToList()))
            .ToList();
        var walkIns = lines.Where(l => l.CustomerId is null).Select(l => l.InvoiceCode).Distinct().ToList();
        return new WithdrawalAudience(traders, walkIns);
    }
}
