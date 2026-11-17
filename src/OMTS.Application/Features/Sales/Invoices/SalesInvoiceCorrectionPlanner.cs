using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>The debt of a profile once the save is done (BR-DEBT-02): what it owes apart from the advances held for open orders, below 0 money owed back.</summary>
internal sealed record CustomerDebtAfterChange(Customer Customer, decimal DebtAfter);

/// <summary>What SALE-07 step 8 will write for a price cut: the new totals, the profile a walk-in invoice gets, what is cut from the advance of the order, and the debt after.</summary>
internal sealed record SalesInvoiceCorrectionPlan(
    decimal PriceCutTotal, decimal Total, Customer? Customer, bool CustomerChanged, IReadOnlyList<CustomerDebtAfterChange> DebtsAfter, AdvanceTrim Trim);

/// <summary>
/// SALE-07 steps 5 and 3 against the database and the rules of the money, for the price cut after a claim: the total comes down (never below 0), a walk-in invoice with money to give back gets a
/// profile, and the debt it leaves is told before the save. Questions and refusals are answered in the order of the use case; nothing is written here.
/// </summary>
public sealed class SalesInvoiceCorrectionPlanner(SalesInvoiceFactsReader factsReader, CustomerDebtReader debts, SalesInvoiceAdvanceTrimmer trimmer)
{
    internal async Task<Result<SalesInvoiceCorrectionPlan>> PlanAsync(SalesInvoice invoice, SalesInvoiceCorrectionEntries entries, bool confirmOwedBack, CancellationToken cancellationToken)
    {
        // Another profile is a data entry correction (SALE-07.5), never a price cut.
        if (entries.CustomerId is not null && invoice.CustomerId is not null)
        {
            return AppError.Invalid("Customer");
        }

        var chosen = await factsReader.ReadCustomerAsync(entries.CustomerId, cancellationToken);
        if (!chosen.IsSuccess)
        {
            return chosen.Error!;
        }

        var customer = chosen.Value ?? invoice.Customer;
        var customerChanged = customer?.Id != invoice.CustomerId;
        var priceCutTotal = invoice.PriceCutTotal + entries.PriceCutAmount;
        var total = InvoiceMoney.Total(invoice.Subtotal, invoice.DiscountAmount, invoice.OtherCharges, priceCutTotal);
        if (total < 0m)
        {
            return AppError.Invalid("Price cut amount");
        }

        // SALE-07.0.E2: amounts that fit their fields one by one can still overflow the columns of the invoice together.
        if (!InvoiceMoney.TotalsFit(invoice.Subtotal, total, priceCutTotal))
        {
            return AppError.Invalid("Total");
        }

        var paid = SalesInvoiceChangeMoney.PaidOf(invoice.Payments);

        // A price cut gives a profile only to a walk-in invoice with money to give back (SALE-07.2): with nothing to give back the profile would only get a debt (BR-SALE-07 (8)).
        if (entries.CustomerId is not null && paid <= total)
        {
            return AppError.Invalid("Customer");
        }

        if (customer is null && paid > total)
        {
            return AppError.Conflict(
                UserMessages.WalkInBuyerProfileNeeded(invoice.Code, paid - total),
                new Dictionary<string, object?> { ["confirmationField"] = "customerId", ["owedBackAmount"] = paid - total });
        }

        var trim = await trimmer.PlanAsync(invoice, total, cancellationToken);
        var debtsAfter = await DebtsAfterAsync(invoice, customer, customerChanged, total, paid, trim, cancellationToken);
        if (!confirmOwedBack && debtsAfter.FirstOrDefault(d => d.DebtAfter < 0m) is { } owedBack)
        {
            var amount = CustomerDebtMath.OwedBack(owedBack.DebtAfter);
            return AppError.Conflict(
                UserMessages.MoneyWillBeOwedBack("correction", owedBack.Customer.Name, amount),
                new Dictionary<string, object?> { ["confirmationField"] = "confirmOwedBack", ["customerName"] = owedBack.Customer.Name, ["owedBackAmount"] = amount });
        }

        return new SalesInvoiceCorrectionPlan(priceCutTotal, total, customer, customerChanged, debtsAfter, trim);
    }

    // The debt of every profile the price cut touches (apart from the advances held for open orders); none for an invoice that stays walk-in.
    private async Task<List<CustomerDebtAfterChange>> DebtsAfterAsync(
        SalesInvoice invoice, Customer? customer, bool customerChanged, decimal total, decimal paid, AdvanceTrim trim, CancellationToken cancellationToken)
    {
        var profiles = new[] { invoice.Customer, customer }.OfType<Customer>().DistinctBy(c => c.Id).ToList();
        var before = await debts.AllocationsAsync(profiles.Select(c => c.Id).ToList(), cancellationToken);
        return profiles.Select(p =>
        {
            var debt = before[p.Id].DebtApartFromReserved;
            var after = !customerChanged ? SalesInvoiceChangeMoney.AfterCorrection(debt, invoice.Total, total) + trim.ReservedGain
                : p.Id == invoice.CustomerId ? SalesInvoiceChangeMoney.AfterLeaving(debt, invoice.Total, paid)
                : SalesInvoiceChangeMoney.AfterComingIn(debt, total, paid);
            return new CustomerDebtAfterChange(p, after);
        }).ToList();
    }
}
