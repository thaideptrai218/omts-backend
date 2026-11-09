using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Harvest;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Returns;

/// <summary>The sales return as the API shows it (CLAIM-03.3 step 2) and what the Sales return screen reads before the save (CLAIM-03 step 1).</summary>
public sealed class SalesReturnViews(
    ISalesReturnRepository salesReturns,
    ISalesInvoiceRepository invoices,
    IQualityClaimRepository claims,
    ICustomerPaymentRepository payments,
    IHarvestLotSalesQuery lotSales,
    CustomerDebtReader debts,
    IAccountRepository accounts,
    SalesInvoiceFactsReader factsReader,
    SalesReturnGuard guard,
    ZoneAccessGuard zoneAccess,
    IStatusCatalog statusCatalog)
{
    /// <summary>The remaining kg of a lot after the cancellation of a return that put <paramref name="backForSaleKg"/> into it: not clamped, as MSG-CLAIM-06 shows it (BR-HARVEST-07).</summary>
    public static decimal RemainingAfterCancel(HarvestLot lot, LotSalesFigures figures, decimal backForSaleKg) =>
        lot.EstimatedKg - figures.InvoicedKg + figures.BackForSaleKg - backForSaleKg;

    /// <summary>The return, read back as saved: the caller has already decided who may see it.</summary>
    public async Task<SalesReturnView> ViewAsync(Guid returnId, CancellationToken cancellationToken)
    {
        var found = await salesReturns.GetDetailAsync(returnId, cancellationToken);
        var names = await accounts.GetDisplayNamesAsync([found!.RecordedByAccountId], cancellationToken);
        var invoiceLines = found.Invoice.Lines.ToDictionary(l => l.Id);
        var lines = found.Lines.OrderBy(l => invoiceLines.GetValueOrDefault(l.InvoiceLineId)?.LineNumber ?? int.MaxValue).Select(l =>
        {
            var line = invoiceLines.GetValueOrDefault(l.InvoiceLineId);
            return new SalesReturnLineView(l.InvoiceLineId, line?.LineNumber, line?.Product.Name, l.HarvestLotId, l.HarvestLot.LotCode, l.ReturnedKg, l.Outcome, l.DiscardedKg);
        }).ToList();

        var backForSale = found.Lines.Where(l => l.Outcome == SalesReturnOutcome.BackForSale).GroupBy(l => l.HarvestLot).Select(g => (Lot: g.Key, Kg: g.Sum(l => l.ReturnedKg))).ToList();
        var figures = await lotSales.FiguresAsync(backForSale.Select(l => l.Lot.Id).ToList(), cancellationToken);
        var lots = backForSale.Select(l =>
        {
            var sales = figures.GetValueOrDefault(l.Lot.Id, LotSalesFigures.None);
            return new SalesReturnLotFigures(l.Lot.Id, l.Lot.LotCode, HarvestLotFigures.Of(l.Lot, sales).RemainingKg, RemainingAfterCancel(l.Lot, sales, found.IsStanding ? l.Kg : 0m));
        }).ToList();

        return new SalesReturnView(
            found.Id, found.Code, found.InvoiceId, found.Invoice.Code, found.ReturnDate, found.Reason, found.Value, found.StatusId, statusCatalog.DisplayNameOf(found.StatusId),
            new AccountRef(found.RecordedByAccountId, names.GetValueOrDefault(found.RecordedByAccountId) ?? string.Empty), found.CancelledAt, found.CancelReason, lines, lots, RowVersionGuard.Encode(found.RowVersion));
    }

    /// <summary>The return for whoever views the sales returns of the invoice's zone (Zone Manager, Owner; an Accountant has no access, §I.4.4): another zone answers 403 MSG-GEN-08.</summary>
    public async Task<Result<SalesReturnView>> GetAsync(Guid returnId, CancellationToken cancellationToken)
    {
        var found = await salesReturns.GetDetailAsync(returnId, cancellationToken);
        if (found is null)
        {
            return AppError.NotFound;
        }

        return await zoneAccess.AllowsAsync(PermissionFunctions.SalesReturns, PermissionAction.View, found.Invoice.ZoneId, cancellationToken)
            ? await ViewAsync(returnId, cancellationToken)
            : AppError.Forbidden;
    }

    /// <summary>SCR-CLAIM-03: the invoice lines with their lots, the value and the debt after it. <paramref name="customerId"/> is the profile chosen for a walk-in invoice.</summary>
    public async Task<Result<SalesReturnPreview>> PreviewAsync(Guid invoiceId, Guid? customerId, CancellationToken cancellationToken)
    {
        var invoice = await invoices.GetAsync(invoiceId, cancellationToken);
        if (await guard.CreateRefusalAsync(invoice, cancellationToken) is { } refused)
        {
            return refused;
        }

        var walkIn = invoice!.CustomerId is null;
        if (!walkIn && customerId is not null)
        {
            return AppError.Invalid("Customer");
        }

        decimal? debt = null;
        decimal? debtAfter = null;
        if (walkIn && customerId is not null)
        {
            var chosen = await factsReader.ReadCustomerAsync(customerId, cancellationToken);
            if (!chosen.IsSuccess)
            {
                return chosen.Error!;
            }

            debt = await debts.DebtAsync(customerId.Value, cancellationToken);
            var own = SalesInvoiceChangeMoney.PaidOf(await payments.ListOfInvoicesAsync([invoice.Id], cancellationToken), null);
            debtAfter = SalesInvoiceChangeMoney.AfterWalkInCancelled(debt.Value, own);
        }
        else if (!walkIn)
        {
            debt = await debts.DebtAsync(invoice.CustomerId!.Value, cancellationToken);
            debtAfter = SalesInvoiceChangeMoney.AfterReturn(debt.Value, invoice.Total);
        }

        var lines = invoice.Lines.OrderBy(l => l.LineNumber).Select(l => new SalesReturnPreviewLine(
            l.Id, l.LineNumber, l.Product.Name, l.HarvestLotId, l.HarvestLot.LotCode, l.HarvestLot.StatusId, statusCatalog.DisplayNameOf(l.HarvestLot.StatusId), l.Kg, l.UnitPrice, l.Amount)).ToList();
        var claimRows = await claims.ListOfInvoiceAsync(invoice.Id, cancellationToken);
        var claimViews = claimRows.Select(c => new SalesReturnPreviewClaim(c.Id, c.Code, c.SentAt, c.StatusId, statusCatalog.DisplayNameOf(c.StatusId), c.Offer)).ToList();
        var ownerMustAgree = !await zoneAccess.IsCooperativeOwnerAsync(cancellationToken);
        return new SalesReturnPreview(
            invoice.Id, invoice.Code, RowVersionGuard.Encode(invoice.RowVersion), walkIn, invoice.Customer is { } c ? new SalesInvoiceCustomerView(c.Id, c.Name, c.PhoneNumber) : null, lines, invoice.Total,
            claimViews, debt, debtAfter, debtAfter is { } after ? CustomerDebtMath.OwedBack(after) : 0m, ownerMustAgree);
    }
}
