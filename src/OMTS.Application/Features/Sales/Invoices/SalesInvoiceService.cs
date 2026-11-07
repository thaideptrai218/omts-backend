using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// SALE-05 – the Zone Manager (or the Owner) creates an invoice at the port, paid in cash, part in cash and the rest on Pay later, or all on Pay later; a walk-in customer pays in full in
/// cash. VietQR (steps 7-8, the pending sale) comes with PR 7 and the sending of SALE-06 with PR 6b. The API checks the role (SALES_INVOICES Create); this checks every zone.
/// </summary>
/// <remarks>
/// A parallel save that wins on a row this one also writes (a lot, the profile, the request), or removes a row it points to (a lot, a product), makes the save fail; the whole use
/// case then runs again on fresh reads, at most <see cref="MaxAttempts"/> times. So a lot closed by the other sale meanwhile answers MSG-GEN-09 naming it (SALE-05.0.E2), and a
/// sale that only shared a lot or a customer is saved. The invoice code is taken once and kept across the attempts; when the attempts end without the invoice, the audit log
/// names the unused codes (BR-SALE-08).
/// </remarks>
public sealed class SalesInvoiceService(
    ISalesInvoiceRepository invoices,
    SalesInvoiceFactsReader factsReader,
    SalesInvoiceWriter writer,
    SalesInvoiceNotifications notifications,
    SalesInvoiceViews views,
    SalesInvoiceReadService reads,
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
        Guid? zoneId = null;
        for (var attempt = 1; ; attempt++)
        {
            var facts = await factsReader.ReadAsync(entries!, cancellationToken);
            if (!facts.IsSuccess)
            {
                await RecordUnusedCodesAsync(code, paymentCode, zoneId, Refused(facts.Error!), cancellationToken);
                return facts.Error!;
            }

            zoneId = facts.Value!.ZoneId;
            var decided = SalesInvoiceDecisions.Decide(entries!, facts.Value!);
            if (!decided.IsSuccess)
            {
                await RecordUnusedCodesAsync(code, paymentCode, zoneId, Refused(decided.Error!), cancellationToken);
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
            catch (Exception exception) when (exception is ConcurrencyConflictException or DuplicateRecordException or ForeignKeyViolationException)
            {
                // Nothing of this attempt was saved: forget it, with its audit rows and its notice, and read again.
                unitOfWork.DiscardChanges();
                if (key is not null && await FirstOfKeyAsync(key, cancellationToken) is { } saved)
                {
                    await RecordUnusedCodesAsync(code, paymentCode, zoneId, "A parallel save with the same deduplication key saved its invoice first.", cancellationToken);
                    return saved;
                }

                if (attempt >= MaxAttempts)
                {
                    // The answer the API gives the exception rethrown below.
                    var answer = exception is DuplicateRecordException ? UserMessages.AlreadyExists("Record") : UserMessages.ConcurrentUpdateByAnotherUser;
                    await RecordUnusedCodesAsync(code, paymentCode, zoneId, $"Parallel saves changed the records of this sale on each of its {MaxAttempts} attempts ({answer.Code}).", cancellationToken);
                    // A row removed on every attempt is a lost race as well: MSG06, as for the other conflicts.
                    if (exception is ForeignKeyViolationException)
                    {
                        throw new ConcurrencyConflictException(exception);
                    }

                    throw;
                }
            }
        }
    }

    /// <summary>The invoice of the key, answered as GET answers it: 403 MSG-GEN-08 for a zone the caller cannot view (BR-SALE-08, GFR-02); null when the key is new.</summary>
    private async Task<Result<SalesInvoiceSaved>?> FirstOfKeyAsync(string key, CancellationToken cancellationToken)
    {
        if (await invoices.FindByDeduplicationKeyAsync(key, cancellationToken) is not { } id)
        {
            return null;
        }

        var invoice = await ReadBackAsync(id, cancellationToken);
        if (!await reads.MayViewAsync(invoice, cancellationToken))
        {
            return AppError.Forbidden;
        }

        return await SavedAsync(invoice, change: 0m, cancellationToken);
    }

    private static string Refused(AppError error) => $"The sale was refused when read again after a parallel save: {error.Message.Code} {error.Message.Text}";

    /// <summary>BR-SALE-08: the codes taken by an attempt that ends without its invoice are named in the audit log, in a save of their own; nothing when no code was taken.</summary>
    private async Task RecordUnusedCodesAsync(string? code, string? paymentCode, Guid? zoneId, string reason, CancellationToken cancellationToken)
    {
        if (code is null)
        {
            return;
        }

        unitOfWork.DiscardChanges();
        writer.RecordUnusedCodes(code, paymentCode, zoneId, reason);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task<SalesInvoice> ReadBackAsync(Guid invoiceId, CancellationToken cancellationToken) =>
        await invoices.GetAsync(invoiceId, cancellationToken) ?? throw new InvalidOperationException($"Sales invoice {invoiceId} was just saved and cannot be read.");

    private async Task<SalesInvoiceSaved> SavedAsync(Guid invoiceId, decimal change, CancellationToken cancellationToken) =>
        await SavedAsync(await ReadBackAsync(invoiceId, cancellationToken), change, cancellationToken);

    /// <summary>The answer, read back as saved: MSG-SALE-06 names the cash recorded and the part added to the debt.</summary>
    private async Task<SalesInvoiceSaved> SavedAsync(SalesInvoice invoice, decimal change, CancellationToken cancellationToken)
    {
        var cash = invoice.Payments.Where(p => p.Method == CustomerPaymentMethod.Cash && p.Kind == CustomerPaymentKind.Payment).Sum(p => p.Amount);
        var subtracted = Math.Max(0m, cash - invoice.Total);
        var later = invoice.PaymentTerm == SalesPaymentTerm.PayLater ? invoice.Total - (cash - subtracted) : 0m;
        var message = UserMessages.SaleCompleted(invoice.Code, cash, later, invoice.Customer?.Name ?? SalesInvoiceNotifications.WalkInCustomerName);
        return new SalesInvoiceSaved(await views.ViewAsync(invoice, cancellationToken), change, subtracted, message.Text, message.Code);
    }
}
