using OMTS.Application.Features.Sales.Payments;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Returns;

/// <summary>BR-CLAIM-03 as pure rules: every line of the invoice exactly once with its outcome, a reason, and the walk-in rule (CLAIM-03.0.E1).</summary>
public static class SalesReturnRules
{
    public const string RecordType = nameof(SalesReturn);
    public const string ObjectName = "Sales return";

    /// <summary>The invalid fields (MSG-GEN-01); the lines are named by field, as each line has one choice.</summary>
    public static List<string> InvalidFields(string? reason, IReadOnlyList<SalesReturnLineEntry?>? lines, SalesInvoice invoice)
    {
        var invalid = new List<string>();
        if (CustomerPaymentRules.Reason(reason) is null)
        {
            invalid.Add("Reason");
        }

        var invoiceLines = invoice.Lines.Select(l => l.Id).ToHashSet();
        var entries = lines ?? [];
        var valid = entries.All(l => l is { InvoiceLineId: { } id, Outcome: { } outcome } && invoiceLines.Contains(id) && Enum.IsDefined(outcome));
        var once = entries.Where(l => l?.InvoiceLineId is not null).GroupBy(l => l!.InvoiceLineId).All(g => g.Count() == 1);
        if (!valid || !once || entries.Count != invoiceLines.Count)
        {
            invalid.Add("Lines");
        }

        return invalid;
    }

    /// <summary>The discarded kg of a return line (BR-HARVEST-07): all its kg when thrown away, 0 when back for sale.</summary>
    public static decimal DiscardedKg(SalesReturnOutcome outcome, decimal kg) => outcome == SalesReturnOutcome.ThrownAway ? kg : 0m;
}
