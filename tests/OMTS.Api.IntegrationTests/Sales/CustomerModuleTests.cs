using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.CustomerDebtTestKit;
using static OMTS.Api.IntegrationTests.Sales.CustomerPaymentTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.VietQrSaleTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// The customer screen's backend: codes, status, delete rules, the list with its figures, payments that name invoices or carry a discount, the VietQR payment that waits for its
/// transfer, debt adjustments, the debt tab and the cash book lines of customer payments.
/// </summary>
public class CustomerModuleTests
{
    private static object PayLater(SaleSetup sale, decimal kg) =>
        Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, kg)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), sale.LotId);

    /// <summary>Two pay-later invoices of the sale's customer: 300,000 (older) and 600,000.</summary>
    private static async Task<(JsonElement Older, JsonElement Newer)> TwoInvoicesAsync(AuthApiFactory api, SaleSetup sale)
    {
        var older = (await sale.Manager.Client.SellAsync(PayLater(sale, 10m))).GetProperty("invoice");
        await api.BackDateAsync(older.Id(), 20);
        var newer = (await sale.Manager.Client.SellAsync(PayLater(sale, 20m))).GetProperty("invoice");
        return (older, newer);
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string url) => await (await client.GetAsync(url)).ReadAsync(HttpStatusCode.OK);

    private static async Task<JsonElement> TabAsync(HttpClient client, Guid customerId, string query = "") => await GetAsync(client, $"/api/customer-debts/{customerId}{query}");

    private static IEnumerable<(string Code, decimal Unpaid)> Unpaid(JsonElement element) =>
        element.GetProperty("unpaidInvoices").EnumerateArray().Select(u => (u.Text("code")!, u.Money("unpaid")));

    private static async Task<Guid> BankAccountAsync(HttpClient client, string number = AccountNumber)
    {
        var response = await client.PostAsJsonAsync("/api/cash-accounts", new { kind = "Bank", bankBin = BankBin, accountNumber = number, holderName = "HTX DEMO" });
        return (await response.ReadAsync(HttpStatusCode.Created)).Id();
    }

    private static async Task<JsonElement> PendingAsync(HttpClient client, Guid customerId, decimal amount, Guid bankAccountId, params object[] extra)
    {
        var body = new Dictionary<string, object?>
        {
            ["customerId"] = customerId, ["amount"] = amount, ["cashAccountId"] = bankAccountId, ["paymentDate"] = PurchaseRequestTestKit.Today.ToString("yyyy-MM-dd"),
        };
        for (var i = 0; i + 1 < extra.Length; i += 2)
        {
            body[(string)extra[i]] = extra[i + 1];
        }

        return await (await client.PostAsJsonAsync("/api/customer-payments/pending-transfer", body)).ReadAsync(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_profile_gets_a_code_or_a_typed_one_and_is_stopped_resumed_and_deleted_by_whether_it_was_used()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;

        var generated = (await (await owner.PostAsJsonAsync("/api/customers", new { name = "Generated", phoneNumber = AcsTestKit.NewPhone() })).ReadAsync(HttpStatusCode.Created)).GetProperty("customer");
        Assert.Matches(new Regex(@"^KH\d{6,}$"), generated.Text("code")!);
        var typed = (await (await owner.PostAsJsonAsync("/api/customers", new { name = "Typed", phoneNumber = AcsTestKit.NewPhone(), code = "kh-x1", customerType = "Company", companyName = "Typed Co" })).ReadAsync(HttpStatusCode.Created)).GetProperty("customer");
        Assert.Equal(("kh-x1", "Company", "Typed Co"), (typed.Text("code"), typed.Text("customerType"), typed.Text("companyName")));

        // A code is unique whatever its letter case, and only letters, digits and - _ . are allowed.
        var again = await owner.PostAsJsonAsync("/api/customers", new { name = "Again", phoneNumber = AcsTestKit.NewPhone(), code = "KH-X1" });
        var taken = await again.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02");
        Assert.Equal(("Code", typed.Id()), (taken.Text("field"), taken.GetProperty("existingId").GetGuid()));
        Assert.Equal("Code", await (await owner.PostAsJsonAsync("/api/customers", new { name = "Bad", phoneNumber = AcsTestKit.NewPhone(), code = "no spaces" })).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        // An unused profile is deleted; one with an invoice is not, and the answer says to stop it instead.
        await (await owner.DeleteAsync($"/api/customers/{typed.Id()}")).ReadAsync(HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/customers/{typed.Id()}")).StatusCode);
        await sale.Manager.Client.SellAsync(PayLater(sale, 10m));
        var used = await (await owner.DeleteAsync($"/api/customers/{sale.CustomerId}")).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-06");
        Assert.True(used.GetProperty("canDiscontinue").GetBoolean());
        Assert.True((await owner.CustomerAsync(sale.CustomerId)).GetProperty("isUsed").GetBoolean());

        // Stopped profiles leave the default list (what the pickers read) and are edited only once resumed.
        var stopped = await (await owner.PostAsync($"/api/customers/{sale.CustomerId}/discontinue", null)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(StatusIds.Usage.Discontinued, stopped.GetProperty("statusId").GetInt32());
        Assert.DoesNotContain(sale.CustomerId, (await GetAsync(owner, "/api/customers?pageSize=100")).GetProperty("items").EnumerateArray().Select(i => i.Id()));
        Assert.Contains(sale.CustomerId, (await GetAsync(owner, "/api/customers?status=Discontinued")).GetProperty("items").EnumerateArray().Select(i => i.Id()));
        Assert.Contains(sale.CustomerId, (await GetAsync(owner, "/api/customers?status=All&pageSize=100")).GetProperty("items").EnumerateArray().Select(i => i.Id()));
        await (await owner.PutAsJsonAsync($"/api/customers/{sale.CustomerId}", new { name = "Edited", phoneNumber = stopped.Text("phoneNumber"), rowVersion = stopped.RowVersion() })).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        await (await owner.PostAsync($"/api/customers/{sale.CustomerId}/resume", null)).ReadAsync(HttpStatusCode.OK);
        Assert.Contains(sale.CustomerId, (await GetAsync(owner, "/api/customers?pageSize=100")).GetProperty("items").EnumerateArray().Select(i => i.Id()));
    }

    [Fact]
    public async Task The_list_filters_sorts_and_sums_and_the_debt_and_sales_columns_are_for_those_who_read_debts_only()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var (older, newer) = await TwoInvoicesAsync(api, sale);
        var idle = await sale.Owner.Client.CreateCustomerAsync();

        var asOwner = await GetAsync(sale.Owner.Client, "/api/customers?currentDebtFrom=1&pageSize=100");
        var row = Assert.Single(asOwner.GetProperty("items").EnumerateArray());
        Assert.Equal((sale.CustomerId, 900_000m, 900_000m, 900_000m), (row.Id(), row.Money("currentDebt"), row.Money("totalSales"), row.Money("netSales")));
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("lastTransactionAt").ValueKind);
        var summary = asOwner.GetProperty("summary");
        Assert.Equal((900_000m, 900_000m, 900_000m), (summary.Money("debt"), summary.Money("totalSales"), summary.Money("netSales")));

        var byDebt = await GetAsync(sale.Owner.Client, "/api/customers?sortBy=currentDebt&sortDir=desc&pageSize=100");
        Assert.Equal(sale.CustomerId, byDebt.GetProperty("items").EnumerateArray().First().Id());
        var none = await GetAsync(sale.Owner.Client, "/api/customers?totalSalesFrom=1000000&pageSize=100");
        Assert.Equal(0, none.GetProperty("total").GetInt32());
        var byType = await GetAsync(sale.Owner.Client, "/api/customers?type=Company&pageSize=100");
        Assert.Equal(0, byType.GetProperty("total").GetInt32());
        Assert.Contains(idle.Id(), (await GetAsync(sale.Owner.Client, "/api/customers?search=" + Uri.EscapeDataString(idle.Text("name")!))).GetProperty("items").EnumerateArray().Select(i => i.Id()));

        // The Zone Manager reads no figure and may not filter or sort by one.
        var asManager = await GetAsync(sale.Manager.Client, "/api/customers?pageSize=100");
        Assert.All(asManager.GetProperty("items").EnumerateArray(), i => Assert.All(new[] { "currentDebt", "totalSales", "netSales" }, p => Assert.Equal(JsonValueKind.Null, i.GetProperty(p).ValueKind)));
        Assert.Equal(JsonValueKind.Null, asManager.GetProperty("summary").ValueKind);
        await (await sale.Manager.Client.GetAsync("/api/customers?currentDebtFrom=1")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await sale.Manager.Client.GetAsync("/api/customers?sortBy=totalSales")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");

        // The export carries the figures for the Owner, never the identity number.
        var export = await sale.Owner.Client.GetAsync("/api/customers/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
    }

    [Fact]
    public async Task A_payment_covers_the_invoices_it_names_before_the_oldest_and_a_discount_covers_like_money()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var (older, newer) = await TwoInvoicesAsync(api, sale);
        var accountant = await api.AllZonesAccountantAsync();
        var client = accountant.Client;

        // Naming the newer invoice leaves the older one unpaid; more than the invoice has left is refused.
        var tooMuch = MoneyBody(sale.CustomerId, 700_000m, "Cash").With("allocations", new[] { new { invoiceId = newer.Id(), amount = 700_000m } }).With("confirmAboveDebt", true);
        Assert.Equal("Allocation 1", await (await client.RecordAsync(tooMuch)).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        var named = await client.PaidAsync(MoneyBody(sale.CustomerId, 300_000m, "Cash").With("allocations", new[] { new { invoiceId = newer.Id(), amount = 300_000m } }));
        var allocations = named.Payment().GetProperty("allocations").EnumerateArray().ToList();
        Assert.Equal((newer.Text("code"), 300_000m), (allocations[0].Text("invoiceCode"), allocations[0].Money("amount")));
        var form = await GetAsync(client, $"/api/customer-payments/form/{sale.CustomerId}");
        Assert.Equal([(older.Text("code")!, 300_000m), (newer.Text("code")!, 300_000m)], Unpaid(form));

        // A payment of 0 with a discount, naming the older invoice, covers it in part; the statement keeps the discount as its own entry.
        var discount = await client.PaidAsync(MoneyBody(sale.CustomerId, 0m).With("method", "Cash").With("bankReference", null).With("discountAmount", 100_000m)
            .With("allocations", new[] { new { invoiceId = older.Id(), amount = 100_000m } }));
        Assert.Equal((0m, 100_000m, 500_000m), (discount.Payment().Money("amount"), discount.Payment().Money("discountAmount"), discount.Money("debtAfter")));
        Assert.Equal([(older.Text("code")!, 200_000m), (newer.Text("code")!, 300_000m)], Unpaid(await GetAsync(client, $"/api/customer-payments/form/{sale.CustomerId}")));
        var tab = await TabAsync(client, sale.CustomerId);
        Assert.Equal(500_000m, tab.Money("debt"));
        Assert.Contains("Discount", tab.GetProperty("entries").GetProperty("items").EnumerateArray().Select(e => e.Text("kind")));

        // Money with no named invoice covers the oldest first, as before.
        await client.PaidAsync(MoneyBody(sale.CustomerId, 250_000m, "Cash"));
        Assert.Equal([(newer.Text("code")!, 250_000m)], Unpaid(await GetAsync(client, $"/api/customer-payments/form/{sale.CustomerId}")));
    }

    [Fact]
    public async Task A_payment_waiting_for_its_transfer_counts_nowhere_until_the_transfer_with_its_code_arrives_for_what_arrived()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var (older, newer) = await TwoInvoicesAsync(api, sale);
        var accountant = await api.AllZonesAccountantAsync();
        var client = accountant.Client;
        var bank = await BankAccountAsync(client);

        var made = await PendingAsync(client, sale.CustomerId, 500_000m, bank);
        var pending = made.Payment();
        var code = pending.Text("code")!;
        Assert.Equal((StatusIds.CustomerPayment.PendingTransfer, 900_000m), (pending.GetProperty("statusId").GetInt32(), made.Money("debtAfter")));
        Assert.Equal([(older.Text("code")!, 300_000m), (newer.Text("code")!, 200_000m)], pending.GetProperty("allocations").EnumerateArray().Select(a => (a.Text("invoiceCode")!, a.Money("amount"))));
        var qr = made.GetProperty("qr");
        Assert.StartsWith("000201", qr.Text("payload"));
        Assert.Equal((code, 500_000m, "999900000001"), (qr.Text("content"), qr.Money("amount"), qr.Text("accountNumber")));
        Assert.Contains(code, qr.Text("payload"));
        Assert.Equal(qr.Text("payload"), (await GetAsync(client, $"/api/customer-payments/{pending.Id()}/qr")).Text("payload"));

        // It counts in no debt, no fund and no unpaid invoice yet.
        Assert.Equal(900_000m, (await TabAsync(client, sale.CustomerId)).Money("debt"));
        var book = await GetAsync(client, "/api/cash-book?fund=All&pageSize=100");
        Assert.DoesNotContain(code, book.GetProperty("items").EnumerateArray().Select(i => i.Text("code")));

        // The transfer with the code completes it with the amount that arrived.
        var done = await api.TransferAsync(null, 500_000m, content: $"MBVCB.123.{code}.CT tu khach", account: "999900000001");
        Assert.Equal("CompletedPayment", done.Outcome());
        var paid = await GetAsync(client, $"/api/customer-payments/{pending.Id()}");
        Assert.Equal((StatusIds.CustomerPayment.Completed, 500_000m), (paid.GetProperty("statusId").GetInt32(), paid.Money("amount")));
        Assert.NotEqual(JsonValueKind.Null, paid.GetProperty("bankReference").ValueKind);
        Assert.Equal(400_000m, (await TabAsync(client, sale.CustomerId)).Money("debt"));
        var line = Assert.Single((await GetAsync(client, "/api/cash-book?fund=Bank&pageSize=100")).GetProperty("items").EnumerateArray(), i => i.Text("code") == code);
        Assert.Equal(("CustomerPayment", "Receipt", 500_000m), (line.Text("source"), line.Text("direction"), line.Money("amount")));
        Assert.Equal(("CustomerPayment", pending.Id()), (line.GetProperty("sourceDocument").Text("kind"), line.GetProperty("sourceDocument").Id()));

        // The same transfer again records nothing more.
        var reference = paid.Text("bankReference")!;
        Assert.Equal("AlreadyRecorded", (await api.TransferAsync(null, 500_000m, reference, $"{code}", "999900000001")).Outcome());
    }

    [Fact]
    public async Task A_short_transfer_cuts_the_allocation_from_the_newest_invoice_and_an_over_one_leaves_the_rest_unallocated_with_a_note()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var (older, newer) = await TwoInvoicesAsync(api, sale);
        var client = (await api.AllZonesAccountantAsync()).Client;
        var bank = await BankAccountAsync(client);

        var shortOne = (await PendingAsync(client, sale.CustomerId, 500_000m, bank)).Payment();
        Assert.Equal("CompletedPayment", (await api.TransferAsync(null, 200_000m, content: shortOne.Text("code"), account: "999900000001")).Outcome());
        var trimmed = await GetAsync(client, $"/api/customer-payments/{shortOne.Id()}");
        Assert.Equal(200_000m, trimmed.Money("amount"));
        Assert.Equal([(older.Text("code")!, 200_000m)], trimmed.GetProperty("allocations").EnumerateArray().Select(a => (a.Text("invoiceCode")!, a.Money("amount"))));
        Assert.Equal("Khách chuyển 200.000, phiếu 500.000", trimmed.Text("note"));
        Assert.Equal(700_000m, (await TabAsync(client, sale.CustomerId)).Money("debt"));

        var overOne = (await PendingAsync(client, sale.CustomerId, 300_000m, bank)).Payment();
        var allocated = overOne.GetProperty("allocations").EnumerateArray().Sum(a => a.Money("amount"));
        Assert.Equal("CompletedPayment", (await api.TransferAsync(null, 450_000m, content: $"{overOne.Text("code")} thanh toan", account: "999900000001")).Outcome());
        var surplus = await GetAsync(client, $"/api/customer-payments/{overOne.Id()}");
        Assert.Equal((450_000m, allocated), (surplus.Money("amount"), surplus.GetProperty("allocations").EnumerateArray().Sum(a => a.Money("amount"))));
        Assert.Equal("Khách chuyển 450.000, phiếu 300.000", surplus.Text("note"));
        Assert.Equal(250_000m, (await TabAsync(client, sale.CustomerId)).Money("debt"));
        Assert.Equal(newer.Text("code"), Assert.Single(Unpaid(await GetAsync(client, $"/api/customer-payments/form/{sale.CustomerId}"))).Code);
    }

    [Fact]
    public async Task A_payment_waiting_for_its_transfer_is_cancelled_with_a_reason_and_its_code_is_then_no_payment()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await TwoInvoicesAsync(api, sale);
        var client = (await api.AllZonesAccountantAsync()).Client;
        var bank = await BankAccountAsync(client);
        var pending = (await PendingAsync(client, sale.CustomerId, 300_000m, bank)).Payment();

        await (await client.CancelAsync(pending.Id(), null)).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        var cancelled = await (await client.CancelAsync(pending.Id(), "Khách đổi ý")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((StatusIds.CustomerPayment.Cancelled, 900_000m), (cancelled.Payment().GetProperty("statusId").GetInt32(), cancelled.Money("debtAfter")));
        Assert.Equal(900_000m, (await TabAsync(client, sale.CustomerId)).Money("debt"));
        await (await client.GetAsync($"/api/customer-payments/{pending.Id()}/qr")).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");

        // A transfer carrying the cancelled code reaches no payment, and the account is not the receiving one: it is ignored.
        Assert.Equal("Ignored", (await api.TransferAsync(null, 300_000m, content: pending.Text("code"), account: "999900000001")).Outcome());
        Assert.Equal(900_000m, (await TabAsync(client, sale.CustomerId)).Money("debt"));
    }

    [Fact]
    public async Task A_code_for_a_payment_needs_a_bank_account_money_and_a_code_the_qr_can_hold()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await TwoInvoicesAsync(api, sale);
        var client = (await api.AllZonesAccountantAsync()).Client;
        var bank = await BankAccountAsync(client);
        var url = "/api/customer-payments/pending-transfer";

        Assert.Equal("Cash account", await (await client.PostAsJsonAsync(url, new { customerId = sale.CustomerId, amount = 100_000m, paymentDate = PurchaseRequestTestKit.Today })).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Amount", await (await client.PostAsJsonAsync(url, new { customerId = sale.CustomerId, amount = 0m, discountAmount = 5_000m, cashAccountId = bank, paymentDate = PurchaseRequestTestKit.Today })).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Code", await (await client.PostAsJsonAsync(url, new { customerId = sale.CustomerId, amount = 100_000m, cashAccountId = bank, code = new string('A', 26), paymentDate = PurchaseRequestTestKit.Today })).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        var typed = await PendingAsync(client, sale.CustomerId, 100_000m, bank, "code", "QR-1");
        Assert.Equal("QR-1", typed.Payment().Text("code"));
        await (await client.PostAsJsonAsync(url, new { customerId = sale.CustomerId, amount = 100_000m, cashAccountId = bank, code = "qr-1", paymentDate = PurchaseRequestTestKit.Today })).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02");
    }

    [Fact]
    public async Task An_adjustment_changes_the_debt_by_its_sign_shows_in_the_debt_tab_and_is_for_the_owner_and_the_accountant_only()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await TwoInvoicesAsync(api, sale);
        var accountant = await api.AllZonesAccountantAsync();

        var up = await (await accountant.Client.PostAsJsonAsync("/api/customer-debts/adjustments", new { customerId = sale.CustomerId, amount = 100_000m, reason = "Phí vận chuyển" })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal((1_000_000m, "MSG-GEN-05"), (up.Money("debtAfter"), up.Text("messageCode")));
        var down = await (await sale.Owner.Client.PostAsJsonAsync("/api/customer-debts/adjustments", new { customerId = sale.CustomerId, amount = -250_000m, reason = "Làm tròn" })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(750_000m, down.Money("debtAfter"));
        Assert.Matches(new Regex(@"^DC\d{6,}$"), up.GetProperty("adjustment").Text("code")!);

        var tab = await TabAsync(accountant.Client, sale.CustomerId);
        Assert.Equal(750_000m, tab.Money("debt"));
        var adjustments = tab.GetProperty("entries").GetProperty("items").EnumerateArray().Where(e => e.Text("kind") == "Adjustment").ToList();
        Assert.Equal([-250_000m, 100_000m], adjustments.Select(e => e.Money("debtChange")));
        Assert.Equal([750_000m, 1_000_000m], adjustments.Select(e => e.Money("debtAfter")));
        Assert.Equal(750_000m, (await sale.Owner.Client.CustomerAsync(sale.CustomerId)).Money("currentDebt"));

        foreach (var (body, field) in new (object, string)[]
        {
            (new { customerId = sale.CustomerId, amount = 0m, reason = "x" }, "Amount"),
            (new { customerId = sale.CustomerId, amount = 10.5m, reason = "x" }, "Amount"),
            (new { customerId = sale.CustomerId, amount = 5m, reason = " " }, "Reason"),
            (new { customerId = Guid.NewGuid(), amount = 5m, reason = "x" }, "Customer"),
        })
        {
            Assert.Equal(field, await (await accountant.Client.PostAsJsonAsync("/api/customer-debts/adjustments", body)).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        }

        await (await sale.Manager.Client.PostAsJsonAsync("/api/customer-debts/adjustments", new { customerId = sale.CustomerId, amount = 5m, reason = "x" })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");

        // A mistake is cancelled with a reason and no longer counts.
        var id = up.GetProperty("adjustment").Id();
        await (await accountant.Client.PostAsJsonAsync($"/api/customer-debts/adjustments/{id}/cancel", new { reason = " " })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        var cancelled = await (await accountant.Client.PostAsJsonAsync($"/api/customer-debts/adjustments/{id}/cancel", new { reason = "Nhập nhầm" })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(650_000m, cancelled.Money("debtAfter"));
        await (await accountant.Client.PostAsJsonAsync($"/api/customer-debts/adjustments/{id}/cancel", new { reason = "Again" })).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.Equal(650_000m, (await TabAsync(accountant.Client, sale.CustomerId)).Money("debt"));
    }

    [Fact]
    public async Task The_debt_tab_lists_every_entry_newest_first_with_the_debt_after_each_and_can_be_narrowed_without_changing_it()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var (older, newer) = await TwoInvoicesAsync(api, sale);
        var client = (await api.AllZonesAccountantAsync()).Client;
        await client.PaidAsync(MoneyBody(sale.CustomerId, 400_000m, "Cash"));

        var tab = await TabAsync(client, sale.CustomerId);
        var entries = tab.GetProperty("entries").GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["Payment", "Invoice", "Invoice"], entries.Select(e => e.Text("kind")));
        Assert.Equal([500_000m, 900_000m, 300_000m], entries.Select(e => e.Money("debtAfter")));
        Assert.Equal((500_000m, 3), (tab.Money("debt"), tab.GetProperty("entries").GetProperty("total").GetInt32()));
        Assert.Equal([(newer.Text("code")!, 500_000m)], Unpaid(tab));
        Assert.NotEqual(JsonValueKind.Null, entries[0].GetProperty("at").ValueKind);

        var invoicesOnly = await TabAsync(client, sale.CustomerId, "?kinds=Invoice");
        Assert.Equal([900_000m, 300_000m], invoicesOnly.GetProperty("entries").GetProperty("items").EnumerateArray().Select(e => e.Money("debtAfter")));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/customer-debts/{Guid.NewGuid()}")).StatusCode);
        await (await sale.Manager.Client.GetAsync($"/api/customer-debts/{sale.CustomerId}")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        Assert.Equal(older.Text("code"), entries[^1].Text("code"));
    }

    [Fact]
    public async Task A_cash_voucher_that_counts_in_the_debt_is_saved_as_a_customer_payment_and_shows_in_the_cash_book()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var (older, newer) = await TwoInvoicesAsync(api, sale);
        var client = (await api.AllZonesAccountantAsync()).Client;
        Dictionary<string, object?> Voucher(decimal amount, string direction) => new()
        {
            ["direction"] = direction, ["fund"] = "Cash", ["occurredAt"] = DateTimeOffset.UtcNow.AddMinutes(-1), ["amount"] = amount, ["partyKind"] = "Customer", ["partyId"] = sale.CustomerId,
            ["countInDebt"] = true,
        };

        var receipt = await (await client.PostAsJsonAsync("/api/cash-book/vouchers", Voucher(400_000m, "Receipt"))).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(("CustomerPayment", CashVoucherTypeIds.ReceiptFromCustomer), (receipt.Text("source"), receipt.GetProperty("type").Id()));
        Assert.Equal(500_000m, (await TabAsync(client, sale.CustomerId)).Money("debt"));
        var paymentId = receipt.GetProperty("sourceDocument").Id();
        var payment = await GetAsync(client, $"/api/customer-payments/{paymentId}");
        Assert.Equal([(older.Text("code")!, 300_000m), (newer.Text("code")!, 100_000m)], payment.GetProperty("allocations").EnumerateArray().Select(a => (a.Text("invoiceCode")!, a.Money("amount"))));
        var line = Assert.Single((await GetAsync(client, "/api/cash-book?fund=Cash&pageSize=100")).GetProperty("items").EnumerateArray(), i => i.Text("code") == receipt.Text("code"));
        Assert.Equal(("Receipt", 400_000m, CashVoucherTypeIds.ReceiptFromCustomer), (line.Text("direction"), line.Money("amount"), line.GetProperty("type").Id()));

        // "Chỉ trừ vào công nợ" names no invoice; the amount may not exceed the debt; a payment out needs money owed back.
        var debtOnly = await (await client.PostAsJsonAsync("/api/cash-book/vouchers", Voucher(100_000m, "Receipt").With("debtMode", "DebtOnly"))).ReadAsync(HttpStatusCode.Created);
        Assert.Empty((await GetAsync(client, $"/api/customer-payments/{debtOnly.GetProperty("sourceDocument").Id()}")).GetProperty("allocations").EnumerateArray());
        Assert.Equal("Amount", await (await client.PostAsJsonAsync("/api/cash-book/vouchers", Voucher(400_001m, "Receipt"))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        await (await client.PostAsJsonAsync("/api/cash-book/vouchers", Voucher(1m, "Payment"))).ProblemAsync(HttpStatusCode.BadRequest, "MSG-DEBT-06");
        await client.PaidAsync(MoneyBody(sale.CustomerId, 400_000m, "Cash"));
        var owedBack = await client.PaidAsync(MoneyBody(sale.CustomerId, 100_000m, "Cash").With("confirmAboveDebt", true));
        Assert.Equal(-100_000m, owedBack.Money("debtAfter"));
        var paidBack = await (await client.PostAsJsonAsync("/api/cash-book/vouchers", Voucher(60_000m, "Payment"))).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(("CustomerPaidBack", CashVoucherTypeIds.PaymentToCustomer), (paidBack.Text("source"), paidBack.GetProperty("type").Id()));
        Assert.Equal(-40_000m, (await TabAsync(client, sale.CustomerId)).Money("debt"));

        var noDebt = await sale.Owner.Client.CreateCustomerAsync();
        var refused = new Dictionary<string, object?> { ["direction"] = "Receipt", ["fund"] = "Cash", ["occurredAt"] = DateTimeOffset.UtcNow.AddMinutes(-1), ["amount"] = 1_000m, ["partyKind"] = "Customer", ["partyId"] = noDebt.Id(), ["countInDebt"] = true };
        Assert.Equal("Count in debt", await (await client.PostAsJsonAsync("/api/cash-book/vouchers", refused)).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Cash account", await (await client.PostAsJsonAsync("/api/cash-book/vouchers", refused.With("fund", "EWallet"))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    [Fact]
    public async Task The_cash_at_the_port_is_confirmed_one_payment_at_a_time_and_stays_out_of_the_cash_book_until_then()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = (await sale.Manager.Client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], Cash(300_000m, "None"), sale.LotId))).GetProperty("invoice");
        var accountant = await api.AllZonesAccountantAsync();
        var paymentId = await api.PortCashOfAsync(invoice);
        var code = (await GetAsync(accountant.Client, $"/api/customer-payments/{paymentId}")).Text("code");

        var before = (await GetAsync(accountant.Client, "/api/cash-book?fund=Cash&pageSize=100")).GetProperty("items").EnumerateArray().Select(i => i.Text("code"));
        Assert.DoesNotContain(code, before);
        var confirmed = await (await accountant.Client.PostAsync($"/api/customer-payments/{paymentId}/confirm", null)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((StatusIds.CustomerPayment.Completed, accountant.Account.Id), (confirmed.Payment().GetProperty("statusId").GetInt32(), confirmed.Payment().GetProperty("confirmedBy").Id()));
        Assert.Contains(code, (await GetAsync(accountant.Client, "/api/cash-book?fund=Cash&pageSize=100")).GetProperty("items").EnumerateArray().Select(i => i.Text("code")));
        await (await accountant.Client.PostAsync($"/api/customer-payments/{paymentId}/confirm", null)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
    }

    [Fact]
    public async Task A_profile_edit_keeps_the_whole_form_hides_the_identity_number_from_the_list_and_checks_the_bank_and_the_formats()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var customer = await owner.CustomerAsync(sale.CustomerId);

        var body = new Dictionary<string, object?>
        {
            ["name"] = "Anh Tùng", ["phoneNumber"] = customer.Text("phoneNumber"), ["rowVersion"] = customer.RowVersion(), ["customerType"] = "Company", ["gender"] = "Male", ["birthDate"] = "1990-05-17",
            ["phoneNumber2"] = "0988111222", ["email"] = "Tung@Example.com", ["facebook"] = "fb.com/tung", ["province"] = "Quảng Ninh", ["ward"] = "Cẩm Đông", ["companyName"] = "Tùng Seafood",
            ["taxCode"] = "0312345678", ["identityNumber"] = "012345678901", ["bankBin"] = BankBin, ["bankAccountNumber"] = "0011223344", ["address"] = "Cẩm Phả", ["note"] = "VIP",
        };
        var saved = (await (await owner.PutAsJsonAsync($"/api/customers/{sale.CustomerId}", body)).ReadAsync(HttpStatusCode.OK)).GetProperty("customer");
        Assert.Equal(("tung@example.com", "012345678901", "Vietcombank", "1990-05-17"), (saved.Text("email"), saved.Text("identityNumber"), saved.Text("bankName"), saved.Text("birthDate")));
        var listed = Assert.Single((await GetAsync(owner, $"/api/customers?search={Uri.EscapeDataString("Anh Tùng")}")).GetProperty("items").EnumerateArray());
        Assert.False(listed.TryGetProperty("identityNumber", out _));
        Assert.False(listed.TryGetProperty("bankAccountNumber", out _));
        var audit = await api.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == sale.CustomerId && e.Action == "Customer.Updated").Select(e => e.After).ToListAsync());
        Assert.DoesNotContain(audit, json => json!.Contains("012345678901"));

        // Birthdays (month and day) and the type are filters; a wrong format or an unknown bank is refused, naming the field.
        var birthdays = await GetAsync(owner, "/api/customers?birthdayFrom=2026-05-01&birthdayTo=2026-05-31&type=Company");
        Assert.Equal(sale.CustomerId, Assert.Single(birthdays.GetProperty("items").EnumerateArray()).Id());
        Assert.Equal(0, (await GetAsync(owner, "/api/customers?birthdayFrom=2026-06-01&birthdayTo=2026-06-30")).GetProperty("total").GetInt32());
        var fresh = await owner.CustomerAsync(sale.CustomerId);
        foreach (var (field, value, expected) in new (string, object, string)[]
        {
            ("taxCode", "12345", "Tax code"), ("identityNumber", "123", "Identity number"), ("email", "not-an-email", "Email"), ("bankBin", "000000", "Bank"), ("birthDate", "2999-01-01", "Birth date"),
        })
        {
            var invalid = new Dictionary<string, object?>(body) { ["rowVersion"] = fresh.RowVersion(), [field] = value };
            if (field != "bankBin")
            {
                invalid["bankBin"] = BankBin;
            }

            Assert.Equal(expected, await (await owner.PutAsJsonAsync($"/api/customers/{sale.CustomerId}", invalid)).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        }
    }

    [Fact]
    public async Task The_photo_is_a_jpeg_or_png_of_at_most_2_mb_and_the_import_reads_a_file_all_or_nothing()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;

        byte[] Png(int size) => [.. new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, .. new byte[size]];
        MultipartFormDataContent Form(byte[] bytes, string name)
        {
            var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", name } };
            return form;
        }

        var done = await (await owner.PutAsync($"/api/customers/{sale.CustomerId}/photo", Form(Png(100), "a.png"))).ReadAsync(HttpStatusCode.OK);
        Assert.NotEqual(JsonValueKind.Null, done.GetProperty("photoUrl").ValueKind);
        await (await owner.PutAsync($"/api/customers/{sale.CustomerId}/photo", Form(Png(2 * 1024 * 1024 + 1), "big.png"))).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        await (await owner.PutAsync($"/api/customers/{sale.CustomerId}/photo", Form(System.Text.Encoding.UTF8.GetBytes("not an image"), "a.png"))).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        var removed = await (await owner.DeleteAsync($"/api/customers/{sale.CustomerId}/photo")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("photoUrl").ValueKind);

        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/customers/import/template")).StatusCode);
    }
}
