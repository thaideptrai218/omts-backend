using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// SALE-05 – the Zone Manager (or the Owner) creates an invoice at the port, paid in cash, part in cash and the rest on Pay later, or all on Pay later; a walk-in customer pays in full in
/// cash. VietQR (steps 7-8, the pending sale) comes with PR 7 and the sending of SALE-06 with PR 6b. The API checks the role (SALES_INVOICES Create); this checks every zone.
/// </summary>
/// <remarks>
/// A parallel save that wins on a row this one also writes (a lot, the profile, the request) makes the save fail; the whole use case then runs again on fresh reads, at most
/// <see cref="MaxAttempts"/> times. So a lot closed by the other sale meanwhile answers MSG-GEN-09 naming it (SALE-05.0.E2), and a sale that only shared a lot or a customer is saved.
/// The invoice code is taken once and kept across the attempts.
/// </remarks>
public sealed class SalesInvoiceService(
    ISalesInvoiceRepository invoices,
    SalesInvoiceFactsReader factsReader,
    SalesInvoiceWriter writer,
    SalesInvoiceNotifications notifications,
    SalesInvoiceViews views,
    IDocumentNumberAllocator numbers,
    ICurrentUser currentUser,
    IUnitOfWork unitOfWork)
{
    // Each round of parallel saves has a winner, so a few attempts cover the sellers of one port; past them, MSG06.
    public const int MaxAttempts = 5;
    private const int MaxKeyLength = 100;

    public async Task<Result<SalesInvoiceSaved>> CreateAsync(CreateSalesInvoiceRequest request, CancellationToken cancellationToken)
    {
        var key = string.IsNullOrWhiteSpace(request.DeduplicationKey) ? null : request.DeduplicationKey.Trim();
        if (key?.Length > MaxKeyLength)
        {
            return AppError.Invalid("Deduplication key");
        }

        if (key is not null && await FirstOfKeyAsync(key, cancellationToken) is { } first)
        {
            return first;
        }

        var (entries, invalid) = SalesInvoiceEntryReader.Read(request);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        string? code = null;
        string? paymentCode = null;
        for (var attempt = 1; ; attempt++)
        {
            var facts = await factsReader.ReadAsync(entries!, cancellationToken);
            if (!facts.IsSuccess)
            {
                return facts.Error!;
            }

            var decided = SalesInvoiceDecisions.Decide(entries!, facts.Value!);
            if (!decided.IsSuccess)
            {
                return decided.Error!;
            }

            var decision = decided.Value!;
            // Taken once the invoice is valid and confirmed, so a refused request spends no number.
            code ??= SalesDocumentCodes.InvoiceCode(await numbers.NextAsync(DocumentSequenceKeys.SalesInvoice, cancellationToken));
            if (decision.Cash.Recorded > 0m)
            {
                paymentCode ??= SalesDocumentCodes.PaymentCode(await numbers.NextAsync(DocumentSequenceKeys.CustomerPayment, cancellationToken));
            }

            var invoice = writer.Write(entries!, facts.Value!, decision, code, decision.Cash.Recorded > 0m ? paymentCode : null, key);
            await notifications.SaleAsync(invoice, decision, currentUser.AccountId!.Value, cancellationToken);
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return await SavedAsync(invoice.Id, decision.Cash.Change, cancellationToken);
            }
            catch (Exception exception) when (exception is ConcurrencyConflictException or DuplicateRecordException)
            {
                // Nothing of this attempt was saved: forget it, with its audit rows and its notice, and read again.
                unitOfWork.DiscardChanges();
                if (key is not null && await FirstOfKeyAsync(key, cancellationToken) is { } saved)
                {
                    return saved;
                }

                if (attempt >= MaxAttempts)
                {
                    throw;
                }
            }
        }
    }

    private async Task<SalesInvoiceSaved?> FirstOfKeyAsync(string key, CancellationToken cancellationToken) =>
        await invoices.FindByDeduplicationKeyAsync(key, cancellationToken) is { } id ? await SavedAsync(id, change: 0m, cancellationToken) : null;

    /// <summary>The answer, read back as saved: MSG-SALE-06 names the cash recorded and the part added to the debt.</summary>
    private async Task<SalesInvoiceSaved> SavedAsync(Guid invoiceId, decimal change, CancellationToken cancellationToken)
    {
        var invoice = await invoices.GetAsync(invoiceId, cancellationToken) ?? throw new InvalidOperationException($"Sales invoice {invoiceId} was just saved and cannot be read.");
        var cash = invoice.Payments.Where(p => p.Method == CustomerPaymentMethod.Cash && p.Kind == CustomerPaymentKind.Payment).Sum(p => p.Amount);
        var subtracted = Math.Max(0m, cash - invoice.Total);
        var later = invoice.PaymentTerm == SalesPaymentTerm.PayLater ? invoice.Total - (cash - subtracted) : 0m;
        var message = UserMessages.SaleCompleted(invoice.Code, cash, later, invoice.Customer?.Name ?? SalesInvoiceNotifications.WalkInCustomerName);
        return new SalesInvoiceSaved(await views.ViewAsync(invoice, cancellationToken), change, subtracted, message.Text, message.Code);
    }
}
