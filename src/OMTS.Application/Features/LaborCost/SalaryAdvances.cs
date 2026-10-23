using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.LaborCost.Calculation;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.LaborCost;

/// <summary>Turns advance records into the ledger entries of one person and computes what is still outstanding.</summary>
public static class SalaryAdvances
{
    /// <summary>Reversals and reversed advances do not count.</summary>
    public static List<AdvanceEntry> Entries(IEnumerable<LaborPayment> records)
    {
        var list = records.Where(p => p.IsAdvance).ToList();
        var reversed = list.Where(p => p.OriginalPaymentId is not null).Select(p => p.OriginalPaymentId!.Value).ToHashSet();
        return list.Where(p => p.OriginalPaymentId is null && !reversed.Contains(p.Id))
            .Select(p => new AdvanceEntry(p.Id, p.PaymentDate, p.CreatedAt, p.AmountPaid))
            .ToList();
    }

    public static List<LaborPayment> Of(IEnumerable<LaborPayment> records, PayeeKey person) =>
        records.Where(p => person.Kind == PayeeKind.Employee ? p.EmployeeId == person.Id : p.WorkerId == person.Id).ToList();

    public static decimal Deducted(DeductedAdvanceTotals totals, PayeeKey person) =>
        person.Kind switch
        {
            PayeeKind.Employee => totals.ByEmployee.GetValueOrDefault(person.Id),
            PayeeKind.Worker => totals.ByWorker.GetValueOrDefault(person.Id),
            _ => 0,
        };

    /// <summary>Remaining amount of each advance of the person (oldest first), given what finalized payrolls deducted.</summary>
    public static IReadOnlyDictionary<Guid, decimal> Remaining(IEnumerable<LaborPayment> records, PayeeKey person, decimal deducted) =>
        AdvanceLedger.Remaining(Entries(Of(records, person)), deducted).ToDictionary(r => r.Advance.Id, r => r.Remaining);
}
