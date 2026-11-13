using System.Text.Json;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>The corrections of invoices as the audit log keeps them (SALE-09.1 step 2, BR-DEBT-04): the event written by <see cref="SalesInvoiceCorrectionWriter"/>, oldest first.</summary>
public sealed class SalesInvoiceCorrectionLog(ISalesInvoiceRepository invoices)
{
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<SalesInvoiceCorrectionView>>> OfAsync(IReadOnlyCollection<Guid> invoiceIds, CancellationToken cancellationToken)
    {
        var events = await invoices.ListCorrectionEventsAsync(invoiceIds, cancellationToken);
        return events.GroupBy(e => e.InvoiceId).ToDictionary(g => g.Key, g => (IReadOnlyList<SalesInvoiceCorrectionView>)g.Select(View).ToList());
    }

    public async Task<IReadOnlyList<SalesInvoiceCorrectionView>> OfAsync(Guid invoiceId, CancellationToken cancellationToken) =>
        (await OfAsync([invoiceId], cancellationToken)).GetValueOrDefault(invoiceId) ?? [];

    private static SalesInvoiceCorrectionView View(SalesInvoiceCorrectionEvent row)
    {
        using var document = JsonDocument.Parse(row.After ?? "{}");
        var after = document.RootElement;
        return new SalesInvoiceCorrectionView(
            Enum.Parse<SalesInvoiceCorrectionKind>(after.GetProperty("Kind").GetString()!),
            BusinessClock.ToBusinessDate(row.OccurredAt),
            row.Reason,
            after.GetProperty("TotalBefore").GetDecimal(),
            after.GetProperty("TotalAfter").GetDecimal());
    }
}
