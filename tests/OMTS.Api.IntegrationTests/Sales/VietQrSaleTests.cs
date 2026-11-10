using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.ReceivingBankAccounts;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.VietQrSaleTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-05 steps 7-8 (BR-SALE-07 (1), GFR-08): the sale paid with the VietQR code of its invoice waits for the transfer, and the transfer saves it.</summary>
public class VietQrSaleTests
{
    // AC-07: HD… of 12,000,000 VND, 4,000,000 in cash and 8,000,000 with its VietQR code: Completed only when the transfer is reported, then two payments Awaiting
    // confirmation; the same bank reference reported again is not recorded twice.
    [Fact]
    public async Task Ac_07_cash_part_and_vietqr_rest_is_completed_only_on_the_transfer_and_a_replayed_reference_is_not_recorded_twice()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.UseReceivingAccountAsync();
        var client = sale.Manager.Client;

        var pending = await client.AwaitTransferAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 400m)], VietQr(4_000_000m)));

        var code = pending.Code();
        Assert.Matches("^HD[0-9]{6}$", code);
        Assert.Equal(("Awaiting", 12_000_000m, 4_000_000m, 8_000_000m), (pending.Text("status"), pending.Money("total"), pending.Money("cashAmount"), pending.Money("toPayAmount")));
        var qr = pending.GetProperty("vietQr");
        Assert.Equal((AccountNumber, BankBin, 8_000_000m, code), (qr.Text("accountNumber"), qr.Text("bankBin"), qr.Money("amount"), qr.Text("content")));
        Assert.Equal(VietQrPayload.Build(BankBin, AccountNumber, 8_000_000, code), qr.Text("payload"));
        // Not saved yet (BR-SALE-08): no invoice, no payment, no debt, the lot still for sale.
        Assert.Equal((0, 0), await api.QueryAsync(async db => (await db.SalesInvoices.CountAsync(), await db.CustomerPayments.CountAsync())));
        Assert.Equal(0m, await client.CustomerDebtAsync(sale.CustomerId));
        Assert.Equal(StatusIds.HarvestLot.Open, await api.QueryAsync(db => db.HarvestLots.Where(l => l.Id == sale.LotId).Select(l => l.StatusId).SingleAsync()));
        Assert.Equal("Awaiting", (await client.PolledAsync(code)).Text("status"));

        var secret = NewSecret();
        using var hooked = api.WithWebhookSecret(secret);
        using var bank = hooked.CreateClient();
        var reference = NewReference();
        var body = NotificationBody(reference, 8_000_000m, $"MBVCB.1234.{code.ToLowerInvariant()}.CT tu DEMO");
        var result = Assert.Single((await (await bank.NotifyAsync(body, secret)).ReadAsync(HttpStatusCode.OK)).GetProperty("transactions").EnumerateArray());
        Assert.Equal(("CompletedSale", code), (result.Outcome(), result.Text("invoiceCode")));

        var done = await client.PolledAsync(code);
        Assert.Equal(("Completed", JsonValueKind.Null), (done.Text("status"), done.GetProperty("vietQr").ValueKind));
        var saved = done.GetProperty("invoice");
        Assert.Equal(($"Invoice {code} is completed: 8,000,000 VND received by VietQR; 4,000,000 VND collected in cash.", "MSG-SALE-06"), (saved.Text("message"), saved.Text("messageCode")));
        var invoice = saved.GetProperty("invoice");
        Assert.Equal((code, "PayNow", 12_000_000m, sale.Manager.Account.Id), (invoice.Text("code"), invoice.Text("paymentTerm"), invoice.Money("total"), invoice.GetProperty("createdBy").Id()));
        var payments = invoice.GetProperty("payments").EnumerateArray().Select(p => (p.Text("method"), p.Money("amount"), p.GetProperty("statusId").GetInt32())).Order().ToList();
        Assert.Equal([("Cash", 4_000_000m, StatusIds.CustomerPayment.AwaitingConfirmation), ("VietQrInvoice", 8_000_000m, StatusIds.CustomerPayment.Completed)], payments);
        var invoiceId = invoice.Id();
        var transfer = Assert.Single(await api.VietQrPaymentsAsync(reference));
        Assert.Equal((invoiceId, sale.CustomerId, (Guid?)null, (string?)null), (transfer.InvoiceId!.Value, transfer.CustomerId!.Value, transfer.RecordedByAccountId, transfer.TransferInvoiceCode));
        var cash = Assert.Single(await api.PaymentsOfAsync(invoiceId), p => p.Method == Domain.Enums.CustomerPaymentMethod.Cash);
        Assert.Equal(sale.Manager.Account.Id, cash.CollectedByAccountId);
        Assert.Equal(invoiceId, await api.QueryAsync(db => db.HarvestLots.Where(l => l.Id == sale.LotId).Select(l => l.ClosedBySalesInvoiceId).SingleAsync()));
        Assert.Equal(0m, await client.CustomerDebtAsync(sale.CustomerId));

        // GBR-02: the invoice as the Zone Manager's sale, the transfer with the system as the actor (POST-6); one notice to the Owner (MSG-SALE-08).
        var audit = await api.QueryAsync(db => db.AuditEvents.AsNoTracking().Where(e => e.RecordCode == code || e.RecordId == transfer.Id).Select(e => new { e.Action, e.ActorAccountId }).ToListAsync());
        Assert.Contains(audit, e => e.Action == "SalesInvoice.Created" && e.ActorAccountId == sale.Manager.Account.Id);
        Assert.Contains(audit, e => e.Action == "CustomerPayment.Recorded" && e.ActorAccountId == null);
        Assert.Equal(1, await api.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == NotificationTypes.SaleCompleted && n.RelatedRecordId == invoiceId)));

        // Reported again with the same reference: not recorded twice.
        var replay = Assert.Single((await (await bank.NotifyAsync(body, secret)).ReadAsync(HttpStatusCode.OK)).GetProperty("transactions").EnumerateArray());
        Assert.Equal(("AlreadyRecorded", transfer.Code), (replay.Outcome(), replay.Text("paymentCode")));
        Assert.Single(await api.VietQrPaymentsAsync(reference));
        Assert.Equal(2, (await api.PaymentsOfAsync(invoiceId)).Count);
        Assert.Equal(1, await api.QueryAsync(db => db.SalesInvoices.CountAsync()));

        // DEBT-02.2: the cash waits for the Accountant; the transfer is Completed at once.
        var accountant = await api.AllZonesAccountantAsync();
        Assert.Equal(1, (await accountant.Client.AwaitingRowsAsync()).Count(r => r.GetProperty("invoice").Text("code") == code));
    }

    // SALE-05.0.E3: VietQR is not offered without a receiving bank account (MSG-SALE-13); cash still works.
    [Fact]
    public async Task Without_a_receiving_bank_account_vietqr_answers_msg_sale_13_and_saves_nothing()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();

        var refused = await (await sale.Manager.Client.PostInvoiceAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], VietQr()))).ReadAsync(HttpStatusCode.Conflict);

        Assert.Equal("MSG-SALE-13", refused.Text("code"));
        Assert.StartsWith("VietQR cannot be used yet: the cooperative has no receiving bank account.", refused.Text("detail"));
        Assert.Equal((0, 0), await api.QueryAsync(async db => (await db.PendingSales.CountAsync(), await db.SalesInvoices.CountAsync())));
        // Cash that covers the total with VietQR chosen needs no transfer: a cash sale.
        await sale.Manager.Client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], VietQr(300_000m)));
    }

    // SALE-05.2: a walk-in customer pays all with the VietQR code; a repeated deduplication key answers the same sale.
    [Fact]
    public async Task A_walk_in_sale_all_by_vietqr_is_saved_without_a_customer_and_a_repeated_key_answers_the_same_sale()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.UseReceivingAccountAsync();
        var body = Invoice(null, [Line(sale.ProductId, sale.LotId, 10m)], VietQr()).With("deduplicationKey", "key-" + AcsTestKit.NewCode());

        var first = await sale.Manager.Client.AwaitTransferAsync(body);
        var again = await sale.Manager.Client.AwaitTransferAsync(body);

        Assert.Equal((first.Code(), true, 300_000m), (again.Code(), again.GetProperty("walkInCustomer").GetBoolean(), again.Money("toPayAmount")));
        Assert.Equal(1, await api.QueryAsync(db => db.PendingSales.CountAsync()));
        Assert.Equal("CompletedSale", (await api.TransferAsync(first.Code(), 300_000m)).Outcome());
        var invoice = (await sale.Manager.Client.PolledAsync(first.Code())).GetProperty("invoice");
        Assert.Equal($"Invoice {first.Code()} is completed: 300,000 VND received by VietQR.", invoice.Text("message"));
        Assert.Equal(JsonValueKind.Null, invoice.GetProperty("invoice").GetProperty("customer").ValueKind);
        // The same key once saved answers the invoice (201).
        var saved = await (await sale.Manager.Client.PostInvoiceAsync(body)).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(first.Code(), saved.GetProperty("invoice").Text("code"));
    }
}
