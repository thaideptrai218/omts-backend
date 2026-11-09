using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Application.Features.Sales.Payments;
using OMTS.Application.Features.Sales.QualityClaims;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Returns;

/// <summary>
/// CLAIM-03 – all the goods of one invoice come back, each line back for sale or thrown away as the Cooperative Owner decided (BR-CLAIM-03). The value of the return takes the invoice out of the
/// customer debt; the invoice is locked (SALE-07, SALE-08); a walk-in invoice and its payments first get the profile of the trader. The return row, the lots, the money and the owner's notice are
/// one save; the push is sent after the commit and never undoes it (GFR-06). A correction or cancellation of the invoice and a payment of the customer change the same rows, so one of them fails
/// with MSG06 or the unique index, and the debt shown is not stale.
/// </summary>
public sealed class SalesReturnService(
    ISalesInvoiceRepository invoices,
    ISalesReturnRepository salesReturns,
    IInvoiceSalesReturnCheck returnsOfInvoices,
    ICustomerPaymentRepository payments,
    SalesReturnGuard guard,
    SalesReturnLots lots,
    SalesReturnViews views,
    SalesReturnNotifications notifications,
    SalesInvoiceFactsReader factsReader,
    CustomerDebtReader debts,
    ZoneAccessGuard zoneAccess,
    IDocumentNumberAllocator numbers,
    ICurrentUser currentUser,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public const string RecordedAction = "SalesReturn.Recorded";

    public async Task<Result<SalesReturnSaved>> CreateAsync(Guid invoiceId, CreateSalesReturnRequest request, CancellationToken cancellationToken)
    {
        var invoice = await invoices.GetForSendingAsync(invoiceId, cancellationToken);
        if (await guard.CreateRefusalAsync(invoice, cancellationToken) is { } refused)
        {
            return refused;
        }

        if (await rowVersions.CheckAsync(invoice!, request.RowVersion, cancellationToken) is { } stale)
        {
            return stale;
        }

        var invalid = SalesReturnRules.InvalidFields(request.Reason, request.Lines, invoice!);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var chosen = await ChosenCustomerAsync(invoice!, request.CustomerId, cancellationToken);
        if (!chosen.IsSuccess)
        {
            return chosen.Error!;
        }

        var isOwner = await zoneAccess.IsCooperativeOwnerAsync(cancellationToken);
        if (!isOwner && !request.OwnerAgreed)
        {
            return AppError.Conflict(
                UserMessages.SalesReturnOwnerAgreed(invoice!.Code, invoice.Total),
                new Dictionary<string, object?> { ["confirmationField"] = "ownerAgreed", ["returnValue"] = invoice.Total });
        }

        var outcomes = request.Lines!.ToDictionary(l => l!.InvoiceLineId!.Value, l => l!.Outcome!.Value);
        var lotChanges = await lots.ApplyAsync(outcomes, invoice!, request.ThrownAwayLots, cancellationToken);
        if (!lotChanges.IsSuccess)
        {
            return lotChanges.Error!;
        }

        var reason = CustomerPaymentRules.Reason(request.Reason)!;
        var (salesReturn, debtAfter) = await RecordAsync(invoice!, reason, outcomes, chosen.Value, cancellationToken);
        audit.Record(new AuditEntry(RecordedAction, SalesReturnRules.RecordType)
        {
            RecordId = salesReturn.Id,
            RecordCode = salesReturn.Code,
            ZoneId = invoice!.ZoneId,
            Reason = reason,
            After = new
            {
                InvoiceId = invoice.Id, InvoiceCode = invoice.Code, salesReturn.Value, salesReturn.ReturnDate, DebtAfter = debtAfter,
                // The Zone Manager's answer to MSG-CLAIM-04; the Owner is not asked.
                OwnerAgreementConfirmed = !isOwner,
                CustomerAttached = chosen.Value?.Id,
                ReopenedLots = lotChanges.Value!.Reopened,
                ClosedLots = lotChanges.Value.Closed,
                Lines = salesReturn.Lines.Select(l => new { l.InvoiceLineId, l.HarvestLotId, l.ReturnedKg, Outcome = l.Outcome.ToString(), l.DiscardedKg }),
            },
        });
        await notifications.NotifyAsync(invoice, salesReturn, debtAfter, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is DuplicateRecordException or ConcurrencyConflictException)
        {
            // A second return of the invoice lost the race (the filtered unique index or the invoice row): it is told so; anything else was changed by someone else (MSG06).
            if (await returnsOfInvoices.HasSalesReturnAsync(invoiceId, cancellationToken))
            {
                return AppError.Conflict(UserMessages.WrongStatus($"Sales invoice {invoice.Code}", "returned"));
            }

            if (exception is ConcurrencyConflictException)
            {
                throw;
            }

            throw new ConcurrencyConflictException(exception);
        }

        var saved = UserMessages.Saved(SalesReturnRules.ObjectName);
        return new SalesReturnSaved(await views.ViewAsync(salesReturn.Id, cancellationToken), debtAfter, CustomerDebtMath.OwedBack(debtAfter), saved.Text, saved.Code);
    }

    // CLAIM-03.2: a walk-in invoice gets a profile, and only a walk-in invoice takes one.
    private async Task<Result<Customer?>> ChosenCustomerAsync(SalesInvoice invoice, Guid? customerId, CancellationToken cancellationToken)
    {
        if (invoice.CustomerId is not null)
        {
            return customerId is null ? Result<Customer?>.Success(null) : AppError.Invalid("Customer");
        }

        return customerId is null ? AppError.Invalid("Customer") : await factsReader.ReadCustomerAsync(customerId, cancellationToken);
    }

    private async Task<(SalesReturn SalesReturn, decimal DebtAfter)> RecordAsync(
        SalesInvoice invoice, string reason, IReadOnlyDictionary<Guid, SalesReturnOutcome> outcomes, Customer? chosen, CancellationToken cancellationToken)
    {
        var ownRows = await payments.GetForEditOfInvoicesAsync([invoice.Id], cancellationToken);
        var customer = chosen ?? invoice.Customer!;
        var before = await debts.DebtAsync(customer.Id, cancellationToken);
        var debtAfter = invoice.CustomerId is null
            // The attached walk-in invoice counts and is taken back at once; only its own payments stay on the profile (BR-DEBT-02).
            ? SalesInvoiceChangeMoney.AfterWalkInCancelled(before, SalesInvoiceChangeMoney.PaidOf(ownRows, null))
            : SalesInvoiceChangeMoney.AfterReturn(before, invoice.Total);

        if (chosen is not null)
        {
            foreach (var payment in SalesInvoiceOwnPayments.Moving(ownRows, invoice.Id, invoice.CustomerId))
            {
                payment.CustomerId = chosen.Id;
            }

            invoice.CustomerId = chosen.Id;
            invoice.Customer = chosen;
        }

        var salesReturn = new SalesReturn
        {
            // Taken once the return is valid and confirmed, so a refused request spends no number.
            Code = SalesDocumentCodes.ReturnCode(await numbers.NextAsync(DocumentSequenceKeys.SalesReturn, cancellationToken)),
            InvoiceId = invoice.Id,
            ReturnDate = timeProvider.Today(),
            Reason = reason,
            Value = invoice.Total,
            RecordedByAccountId = currentUser.AccountId!.Value,
        };
        foreach (var line in invoice.Lines)
        {
            var outcome = outcomes[line.Id];
            salesReturn.Lines.Add(new SalesReturnLine
            {
                InvoiceLineId = line.Id,
                HarvestLotId = line.HarvestLotId,
                ReturnedKg = line.Kg,
                Outcome = outcome,
                DiscardedKg = SalesReturnRules.DiscardedKg(outcome, line.Kg),
            });
        }

        invoice.Returns.Add(salesReturn);
        salesReturns.Add(salesReturn);
        // The invoice and the profile join the save: a correction, a cancellation or a payment at the same moment makes one side fail, so the figures shown are not stale.
        unitOfWork.MarkChanged(invoice);
        unitOfWork.MarkChanged(customer);
        return (salesReturn, debtAfter);
    }
}
