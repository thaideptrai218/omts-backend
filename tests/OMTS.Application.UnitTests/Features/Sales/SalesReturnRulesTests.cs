using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Application.Features.Sales.Returns;
using OMTS.Application.Features.Traceability.Tracing;
using OMTS.Application.Features.Traceability.Withdrawal;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Application.UnitTests.Features.Sales.CustomerDebtAllocatorTests;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>CLAIM-03 pure rules: the return covers its invoice in the allocation (BR-DEBT-05), the statement entry (BR-DEBT-04), the offers match the refusals, and the fields of the request.</summary>
public class SalesReturnRulesTests
{
    private static readonly DateOnly Today = new(2026, 11, 8);

    [Fact]
    public void A_return_covers_its_own_invoice_in_full_and_its_payment_goes_to_the_oldest_invoices()
    {
        var returned = Invoice("HD000002", 5, 1_000_000m) with { ReturnValue = 1_000_000m };
        var other = Invoice("HD000001", 10, 2_000_000m);

        var allocation = CustomerDebtAllocator.Allocate([returned, other], [Payment(1_000_000m, returned, daysAgo: 5)], Today);

        // The payment of the returned invoice is not needed by it, so it covers the older invoice instead.
        Assert.Equal(1_000_000m, allocation.Debt);
        var unpaid = Assert.Single(allocation.UnpaidInvoices);
        Assert.Equal(("HD000001", 1_000_000m), (unpaid.Code, unpaid.Unpaid));
        Assert.DoesNotContain(allocation.UnpaidInvoices, i => i.Code == "HD000002");
    }

    [Fact]
    public void A_return_above_the_debt_leaves_money_owed_back_and_nothing_unpaid()
    {
        var returned = Invoice("HD000001", 5, 1_000_000m) with { ReturnValue = 1_000_000m };

        var allocation = CustomerDebtAllocator.Allocate([returned], [Payment(1_000_000m, returned, daysAgo: 5)], Today);

        Assert.Equal(-1_000_000m, allocation.Debt);
        Assert.Empty(allocation.UnpaidInvoices);
    }

    [Fact]
    public void The_statement_lists_a_return_on_its_date_and_a_cancelled_one_counts_nothing()
    {
        var invoice = Invoice("HD000001", 5, 1_000_000m) with { ReturnValue = 1_000_000m };
        var standing = new CustomerLedgerReturn(Guid.NewGuid(), Guid.NewGuid(), "TL000001", invoice.Id, invoice.Code, Today.AddDays(-2), 1_000_000m, false, null, null);
        var cancelled = standing with { Id = Guid.NewGuid(), Code = "TL000002", Cancelled = true, CancelledAt = DateTimeOffset.UtcNow };

        var lines = CustomerStatementLedger.Build([invoice], [], Today.AddDays(-10), Today, returns: [standing, cancelled]);

        Assert.Equal(
            [(CustomerStatementEntryKind.Invoice, 1_000_000m, 1_000_000m), (CustomerStatementEntryKind.Return, -1_000_000m, 0m), (CustomerStatementEntryKind.Return, 0m, 0m)],
            lines.Entries.Select(e => (e.Kind, e.DebtChange, e.DebtAfter)).ToList());
        Assert.Equal(0m, lines.ClosingDebt);
    }

    [Theory]
    [InlineData(StatusIds.SalesInvoice.Completed, false, true, null)]
    [InlineData(StatusIds.SalesInvoice.Completed, false, false, SalesReturnRefusal.NotPermitted)]
    [InlineData(StatusIds.SalesInvoice.Cancelled, false, true, SalesReturnRefusal.InvoiceCancelled)]
    [InlineData(StatusIds.SalesInvoice.Completed, true, true, SalesReturnRefusal.HasSalesReturn)]
    public void The_create_refusal_is_the_rule_the_offer_uses(int status, bool hasReturn, bool may, SalesReturnRefusal? expected)
    {
        Assert.Equal(expected, SalesInvoiceAllowedActions.CreateReturnRefusal(status, hasReturn, may));
        var offered = SalesInvoiceAllowedActions.Of(
            new SalesInvoiceActionFacts(status, StatusIds.MessageDelivery.Sent, true, hasReturn), new SalesInvoiceCallerRights(false, true, may, true));
        Assert.Equal(expected is null, offered.Contains(SalesInvoiceAction.CreateReturn));
    }

    [Theory]
    [InlineData(true, true, null)]
    [InlineData(true, false, SalesReturnRefusal.NotPermitted)]
    [InlineData(false, true, SalesReturnRefusal.NoSalesReturn)]
    public void The_cancel_refusal_is_the_rule_the_offer_uses(bool stands, bool owner, SalesReturnRefusal? expected)
    {
        Assert.Equal(expected, SalesInvoiceAllowedActions.CancelReturnRefusal(stands, owner));
        var offered = SalesInvoiceAllowedActions.Of(
            new SalesInvoiceActionFacts(StatusIds.SalesInvoice.Completed, StatusIds.MessageDelivery.Sent, true, stands), new SalesInvoiceCallerRights(owner, true, true, true));
        Assert.Equal(expected is null, offered.Contains(SalesInvoiceAction.CancelReturn));
    }

    [Fact]
    public void Has_a_sales_return_is_a_return_that_is_not_cancelled()
    {
        var invoice = new SalesInvoice();
        Assert.False(SalesInvoiceAllowedActions.HasSalesReturn(invoice));

        invoice.Returns.Add(new SalesReturn { StatusId = StatusIds.SalesReturn.Cancelled });
        Assert.False(SalesInvoiceAllowedActions.HasSalesReturn(invoice));

        invoice.Returns.Add(new SalesReturn());
        Assert.True(SalesInvoiceAllowedActions.HasSalesReturn(invoice));
    }

    [Fact]
    public void Every_line_is_needed_once_and_the_reason_is_required()
    {
        var invoice = new SalesInvoice { Lines = [new SalesInvoiceLine(), new SalesInvoiceLine()] };
        var lines = invoice.Lines.Select(l => (SalesReturnLineEntry?)new SalesReturnLineEntry(l.Id, SalesReturnOutcome.BackForSale)).ToList();

        Assert.Empty(SalesReturnRules.InvalidFields("Dead on arrival", lines, invoice));
        Assert.Equal(["Reason"], SalesReturnRules.InvalidFields(" ", lines, invoice));
        Assert.Equal(["Lines"], SalesReturnRules.InvalidFields("x", lines.Take(1).ToList(), invoice));
        Assert.Equal(["Lines"], SalesReturnRules.InvalidFields("x", [lines[0], lines[0]], invoice));
        Assert.Equal(["Lines"], SalesReturnRules.InvalidFields("x", [lines[0], new SalesReturnLineEntry(Guid.NewGuid(), SalesReturnOutcome.BackForSale)], invoice));
        Assert.Equal(["Lines"], SalesReturnRules.InvalidFields("x", [lines[0], new SalesReturnLineEntry(invoice.Lines.Last().Id, null)], invoice));
        Assert.Equal(10m, SalesReturnRules.DiscardedKg(SalesReturnOutcome.ThrownAway, 10m));
        Assert.Equal(0m, SalesReturnRules.DiscardedKg(SalesReturnOutcome.BackForSale, 10m));
    }

    [Fact]
    public void A_returned_invoice_tells_no_trader_of_a_withdrawal()
    {
        LotSaleLine Line(string invoice, Guid customer, bool returned) =>
            new(Guid.NewGuid(), invoice, DateTimeOffset.UtcNow, Today, Guid.NewGuid(), "KV01", customer, "DEMO", "999900000001", 1, "Oysters", 1m, 1m, 1m, null, returned);

        var audience = WithdrawalAudience.Of([Line("HD000001", Guid.NewGuid(), returned: true), Line("HD000002", Guid.NewGuid(), returned: false)]);

        Assert.Equal("HD000002", Assert.Single(Assert.Single(audience.Traders).InvoiceCodes));
    }

    [Fact]
    public void The_notice_tells_the_debt_and_asks_the_accountant_for_the_money_back()
    {
        var owner = UserMessages.SalesReturnRecorded("DEMO manager", "TL000001", "HD000001", "DEMO trader", 500_000m, 10m, 0m, -500_000m, forAccountant: false);
        var accountant = UserMessages.SalesReturnRecorded("DEMO manager", "TL000001", "HD000001", "DEMO trader", 500_000m, 10m, 0m, -500_000m, forAccountant: true);
        var noDebt = UserMessages.SalesReturnRecorded("DEMO manager", "TL000001", "HD000001", "DEMO trader", 500_000m, 10m, 5m, 100_000m, forAccountant: true);

        Assert.Equal("MSG-CLAIM-05", owner.Code);
        Assert.Contains("The cooperative now owes DEMO trader 500,000 VND", owner.Text);
        Assert.DoesNotContain("Please pay", owner.Text);
        Assert.Contains("Please pay the money back", accountant.Text);
        Assert.Contains("10 kg back for sale, 5 kg thrown away", noDebt.Text);
        Assert.Contains("DEMO trader now owes the cooperative 100,000 VND.", noDebt.Text);
        Assert.DoesNotContain("Please pay", noDebt.Text);
    }
}
