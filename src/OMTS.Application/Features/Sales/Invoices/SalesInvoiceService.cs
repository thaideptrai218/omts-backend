using OMTS.Application.Features.Sales.Payments;
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
/// SALE-05 – the Zone Manager (or the Owner) creates an invoice at the port, paid in cash, part in cash and the rest on Pay later, or all on Pay later; or with the VietQR code of
/// the invoice for all or the rest after a cash part (steps 7-8): the code is kept and the sale waits for the transfer (<see cref="PendingSale"/>, saved by the bank notification),
/// unless the Zone Manager changes the payment method (<see cref="ChangePaymentAsync"/>) or stops the sale (<see cref="PendingSaleService"/>). Steps 9-11 (the QR question, the QR
/// code and SALE-06) follow the save as their own call, <see cref="SalesInvoiceSendingService.FinishAsync"/>. The API checks the role (SALES_INVOICES Create); this checks every zone.
/// </summary>
/// <remarks>
/// A parallel save that wins on a row this one also writes (a lot, the profile, the request, the sale awaiting its transfer), or removes a row it points to (a lot, a product),
/// makes the save fail; the whole use case then runs again on fresh reads, at most <see cref="MaxAttempts"/> times. So a lot closed by the other sale meanwhile answers MSG-GEN-09
/// naming it (SALE-05.0.E2), and a sale that only shared a lot or a customer is saved. The invoice code is taken once and kept across the attempts; when the attempts end without
/// the invoice, the audit log names the unused codes (BR-SALE-08).
/// </remarks>
public sealed class SalesInvoiceService(
    ISalesInvoiceRepository invoices,
    IPendingSaleRepository pendingSales,
    ICooperativeRepository cooperatives,
    SalesInvoiceFactsReader factsReader,
    SalesInvoiceWeighingReader weighingReader,
    SalesInvoiceWriter writer,
    PendingSaleWriter pendingWriter,
    SalesInvoiceNotifications notifications,
    SalesInvoiceViews views,
    PendingSaleViews pendingViews,
    SalesInvoiceReadService reads,
    IDocumentNumberAllocator numbers,
    CustomerPaymentCodeService paymentCodes,
    ICurrentUser currentUser,
    IUnitOfWork unitOfWork)
{
    // Each round of parallel saves has a winner, so a few attempts cover the sellers of one port; past them, MSG06.
    public const int MaxAttempts = 5;
    private const int MaxKeyLength = 100;

    public async Task<Result<SalesInvoiceCreated>> CreateAsync(CreateSalesInvoiceRequest request, CancellationToken cancellationToken)
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

        // Một hóa đơn lập từ lần cân đã chốt lấy số kg của lần cân đó.
        var weighed = await weighingReader.FillKgAsync(request, cancellationToken);
        if (!weighed.IsSuccess)
        {
            return weighed.Error!;
        }

        var (entries, invalid) = SalesInvoiceEntryReader.Read(weighed.Value!);
        return invalid.Count > 0 ? AppError.InvalidFields(invalid) : await SaveAsync(weighed.Value!, entries!, key, pendingCode: null, cancellationToken);
    }

    /// <summary>
    /// SALE-05.0.E4 step 2: the transfer has not arrived and the Zone Manager takes Cash, part in cash with Pay later, or Pay later instead; the sale is checked and saved as a new
    /// one at the port, with the code it was given. A transfer that still arrives then pays this invoice (BR-SALE-07 (2)).
    /// </summary>
    public async Task<Result<SalesInvoiceCreated>> ChangePaymentAsync(string code, ChangePendingSalePaymentRequest body, CancellationToken cancellationToken)
    {
        if (await pendingSales.GetAsync(code, cancellationToken) is not { } sale)
        {
            return AppError.NotFound;
        }

        if (!await pendingViews.MayUseAsync(sale, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (sale.Status != PendingSaleStatus.Awaiting)
        {
            return PendingSaleService.NotAwaiting(sale);
        }

        if (body.Payment?.Rest == SalesInvoiceRestPayment.VietQr)
        {
            return AppError.Invalid("Payment", "The VietQR code of this sale is already shown: choose Cash or Pay later.");
        }

        // The profile the sale names now: a merge while the transfer was awaited moved the sale (and its purchase request) to the kept profile (SALE-04.3).
        var request = PendingSaleSnapshot.Read(sale).Request with { CustomerId = sale.CustomerId, Payment = body.Payment, DeduplicationKey = null };
        var (entries, invalid) = SalesInvoiceEntryReader.Read(request);
        return invalid.Count > 0 ? AppError.InvalidFields(invalid) : await SaveAsync(request, entries!, key: null, sale.Code, cancellationToken);
    }

    private async Task<Result<SalesInvoiceCreated>> SaveAsync(
        CreateSalesInvoiceRequest request, SalesInvoiceEntries entries, string? key, string? pendingCode, CancellationToken cancellationToken)
    {
        var code = pendingCode;
        string? paymentCode = null;
        Guid? zoneId = null;
        for (var attempt = 1; ; attempt++)
        {
            var pending = pendingCode is null ? null : await pendingSales.GetForEditAsync(pendingCode, cancellationToken);
            if (pending is { Status: not PendingSaleStatus.Awaiting })
            {
                // The transfer arrived (or the sale was stopped) while this attempt read the sale.
                return PendingSaleService.NotAwaiting(pending);
            }

            var facts = await factsReader.ReadAsync(entries, cancellationToken);
            if (!facts.IsSuccess)
            {
                await RecordUnusedCodesAsync(code, pendingCode, paymentCode, zoneId, Refused(facts.Error!), cancellationToken);
                return facts.Error!;
            }

            zoneId = facts.Value!.ZoneId;
            var decided = SalesInvoiceDecisions.Decide(entries, facts.Value!);
            if (!decided.IsSuccess)
            {
                await RecordUnusedCodesAsync(code, pendingCode, paymentCode, zoneId, Refused(decided.Error!), cancellationToken);
                return decided.Error!;
            }

            var decision = decided.Value!;
            if (decision.AwaitsTransfer && (await cooperatives.GetCurrentAsync(cancellationToken))?.BankAccount is null)
            {
                // SALE-05.0.E3: VietQR is offered only once the receiving bank account is set up (BR-DEBT-01).
                await RecordUnusedCodesAsync(code, pendingCode, paymentCode, zoneId, $"{MessageCodes.Sale13}: no receiving bank account.", cancellationToken);
                return AppError.Conflict(UserMessages.VietQrNeedsReceivingAccount);
            }

            // Taken once the invoice is valid and confirmed, so a refused request spends no number.
            code ??= SalesDocumentCodes.InvoiceCode(await numbers.NextAsync(DocumentSequenceKeys.SalesInvoice, cancellationToken));
            if (decision.Cash.Recorded > 0m && !decision.AwaitsTransfer)
            {
                paymentCode ??= await paymentCodes.NextAsync(cancellationToken);
            }

            Func<Task<SalesInvoiceCreated>> answer;
            if (decision.AwaitsTransfer)
            {
                // SALE-05 step 7: the code is kept and the sale waits; the cash part is recorded with the invoice when the transfer arrives (SALE-05.7 step 3).
                var sale = pendingWriter.Add(request, facts.Value!, decision, code, key);
                answer = async () => new SalesInvoiceCreated(null, await pendingViews.ViewAsync(await ReadPendingAsync(sale.Code, cancellationToken), cancellationToken));
            }
            else
            {
                var invoice = writer.Write(entries, facts.Value!, decision, code, decision.Cash.Recorded > 0m ? paymentCode : null, key);
                if (pending is not null)
                {
                    pendingWriter.End(pending, PendingSaleStatus.PaymentChanged, invoice.Id, currentUser.AccountId);
                }

                await notifications.SaleAsync(invoice, decision, currentUser.AccountId!.Value, cancellationToken);
                answer = async () => new SalesInvoiceCreated(await views.SavedAsync(await ReadBackAsync(invoice.Id, cancellationToken), decision.Cash.Change, false, cancellationToken), null);
            }

            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return await answer();
            }
            catch (Exception exception) when (exception is ConcurrencyConflictException or DuplicateRecordException or ForeignKeyViolationException)
            {
                // Nothing of this attempt was saved: forget it, with its audit rows and its notice, and read again.
                unitOfWork.DiscardChanges();
                if (key is not null && await FirstOfKeyAsync(key, cancellationToken) is { } saved)
                {
                    await RecordUnusedCodesAsync(code, pendingCode, paymentCode, zoneId, "A parallel save with the same deduplication key saved its invoice first.", cancellationToken);
                    return saved;
                }

                if (attempt >= MaxAttempts)
                {
                    // The answer the API gives the exception rethrown below.
                    var refused = exception is DuplicateRecordException ? UserMessages.AlreadyExists("Record") : UserMessages.ConcurrentUpdateByAnotherUser;
                    await RecordUnusedCodesAsync(code, pendingCode, paymentCode, zoneId, $"Parallel saves changed the records of this sale on each of its {MaxAttempts} attempts ({refused.Code}).", cancellationToken);
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

    /// <summary>
    /// The invoice of the key, answered as GET answers it: 403 MSG-GEN-08 for a zone the caller cannot view (BR-SALE-08, GFR-02); or the sale of the key still awaiting its
    /// transfer (202 again). Null when the key is new.
    /// </summary>
    private async Task<Result<SalesInvoiceCreated>?> FirstOfKeyAsync(string key, CancellationToken cancellationToken)
    {
        if (await invoices.FindByDeduplicationKeyAsync(key, cancellationToken) is { } id)
        {
            var invoice = await ReadBackAsync(id, cancellationToken);
            return await reads.MayViewAsync(invoice, cancellationToken)
                ? new SalesInvoiceCreated(await views.SavedAsync(invoice, change: 0m, savedOnTransfer: false, cancellationToken), null)
                : AppError.Forbidden;
        }

        if (await pendingSales.FindCodeByDeduplicationKeyAsync(key, cancellationToken) is not { } code)
        {
            return null;
        }

        var sale = await ReadPendingAsync(code, cancellationToken);
        if (!await pendingViews.MayUseAsync(sale, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var view = await pendingViews.ViewAsync(sale, cancellationToken);
        return view.Invoice is { } saved ? new SalesInvoiceCreated(saved, null) : new SalesInvoiceCreated(null, view);
    }

    private static string Refused(AppError error) => $"The sale was refused when read again after a parallel save: {error.Message.Code} {error.Message.Text}";

    /// <summary>
    /// BR-SALE-08: the codes taken by an attempt that ends without its invoice are named in the audit log, in a save of their own; nothing when no code was taken. The code of a
    /// sale awaiting its transfer stays that sale's.
    /// </summary>
    private async Task RecordUnusedCodesAsync(string? code, string? pendingCode, string? paymentCode, Guid? zoneId, string reason, CancellationToken cancellationToken)
    {
        if (code is null || (code == pendingCode && paymentCode is null))
        {
            return;
        }

        unitOfWork.DiscardChanges();
        writer.RecordUnusedCodes(code, paymentCode, zoneId, code == pendingCode ? $"{reason} The invoice code stays with the sale awaiting its transfer." : reason);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task<SalesInvoice> ReadBackAsync(Guid invoiceId, CancellationToken cancellationToken) =>
        await invoices.GetAsync(invoiceId, cancellationToken) ?? throw new InvalidOperationException($"Sales invoice {invoiceId} was just saved and cannot be read.");

    private async Task<PendingSale> ReadPendingAsync(string code, CancellationToken cancellationToken) =>
        await pendingSales.GetAsync(code, cancellationToken) ?? throw new InvalidOperationException($"Pending sale {code} was just saved and cannot be read.");
}
