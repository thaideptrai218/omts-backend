using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// QA of PURCHASE-04/05 on the API (report qa-261104-supplier-payments-debts.md). A test whose comment names a FINDING is a
/// defect accepted for now, and skipped; the others pin what was checked, found right or fixed.
/// </summary>
public class SupplierPaymentsDebtsQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Debts = "/api/supplier-debts";
    private const string Payments = "/api/supplier-payments";

    private static ReceiptBody Dated(PurchasingWorld world, DateOnly date, decimal amount) =>
        world.Body(new LineBody(world.Other, 1, amount)) with { ReceiptDate = date, Complete = true };

    // PURCHASE-04.2: the export holds the debt list. BR-PURCHASE-01/08 set no earliest date, so a mistyped year is saved, and the list then carries it as
    // "Oldest unpaid receipt". Excel has no such date (ClosedXML throws before year 100), so it is text in the same format and the other suppliers keep their file.
    [Theory]
    [InlineData("Receipt", 26, "04/11/0026 00:00")]
    [InlineData("PaidBack", 26, "04/11/0026 00:00")]
    [InlineData("PaidBack", 1, "04/11/0001 00:00")]
    [InlineData("PaidBack", 99, "04/11/0099 00:00")]
    [InlineData("PaidBack", 100, "04/11/0100 00:00")]
    [InlineData("PaidBack", 1899, "04/11/1899 00:00")]
    [InlineData("PaidBack", 1900, null)]
    public async Task A_document_with_a_mistyped_year_is_written_as_text_and_does_not_lose_the_debt_export_of_every_supplier(string document, int year, string? text)
    {
        // A database of its own: the export lists every supplier.
        await using var isolated = new AuthApiFactory();
        var world = await isolated.WorldAsync();
        var date = new DateOnly(year, 11, 4);
        var tag = NewCode();
        var healthy = world with { SupplierId = await world.Owner.CreateSupplierAsync(name: $"Healthy {tag}") };
        await healthy.CompletedAsync(new LineBody(world.Other, 1, 1_000));
        Assert.Equal(HttpStatusCode.OK, (await world.Owner.GetAsync($"{Debts}/export")).StatusCode);

        var mistyped = await world.Owner.CreateSupplierAsync(name: $"Mistyped {tag}");
        if (document == "Receipt")
        {
            await world.Owner.CreateReceiptAsync(Dated(world with { SupplierId = mistyped }, date, 1_000));
        }
        else
        {
            await (world with { SupplierId = mistyped }).PayAsync(1_000, date, "PaidBack");
        }

        var list = await (await world.Owner.GetAsync(Debts)).ReadAsync(HttpStatusCode.OK);
        Assert.Contains(list.GetProperty("page").GetProperty("items").EnumerateArray(), r => r.GetProperty("supplier").Id() == mistyped);

        var export = await world.Owner.GetAsync($"{Debts}/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        using var workbook = new XLWorkbook(await export.Content.ReadAsStreamAsync());
        var rows = workbook.Worksheet("Supplier debts").RowsUsed().Skip(1).ToList();
        Assert.Single(rows, r => r.Cell(1).GetString() == $"Healthy {tag}");
        var oldest = rows.Single(r => r.Cell(1).GetString() == $"Mistyped {tag}").Cell(6);
        if (text is null)
        {
            Assert.Equal(date.ToDateTime(TimeOnly.MinValue), oldest.GetDateTime());
        }
        else
        {
            Assert.Equal((true, text), (oldest.Value.IsText, oldest.GetString()));
        }
    }

    // PURCHASE-04.2 with a period (MSG-GEN-01 or a file, PURCHASE-04.0.E1): the statement sheet carries the early dates of the period and of what happened in it.
    [Theory]
    [InlineData("0001-01-01", "0001-01-02", "01/01/0001 00:00", "02/01/0001 00:00")]
    [InlineData("0099-12-30", "0100-01-02", "30/12/0099 00:00", "02/01/0100 00:00")]
    public async Task An_export_period_before_year_100_gives_a_file_with_its_dates_as_text(string from, string to, string fromText, string toText)
    {
        var world = await factory.WorldAsync();
        await world.PayAsync(7, DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture), "PaidBack");

        var export = await world.Owner.GetAsync($"{Debts}/export?supplierId={world.SupplierId}&from={from}&to={to}");

        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        using var workbook = new XLWorkbook(await export.Content.ReadAsStreamAsync());
        var rows = workbook.Worksheet("Statement").RowsUsed().Skip(1).ToList();
        Assert.Equal([fromText, toText, toText], rows.Select(r => r.Cell(1).GetString()));
        Assert.Equal(7m, rows[^1].Cell(9).GetValue<decimal>());
    }

    // Client list §2: bankReference is "ignored (null) for Cash"; PURCHASE-05.0 step 2 asks for it only for a transfer.
    [Fact]
    public async Task A_cash_payment_ignores_a_long_bank_reference_as_the_client_list_says()
    {
        var world = await factory.WorldAsync();

        var payment = await world.Owner.PostPaymentAsync(new PaymentBody
        {
            SupplierId = world.SupplierId, Amount = 10, ConfirmAdvance = true, Method = "Cash", BankReference = new string('r', 101),
        });

        var body = await payment.ReadAsync(HttpStatusCode.Created);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("bankReference").ValueKind);
    }

    // SupplierDebtService.RowsAsync: what was paid in advance comes last, largest first (no SRS order).
    [Fact]
    public async Task Among_suppliers_paid_in_advance_the_largest_advance_comes_first()
    {
        var tag = NewCode();
        var owner = await factory.PurchasingOwnerAsync();
        var small = await factory.WorldAsync(owner) with { SupplierId = await owner.CreateSupplierAsync(name: $"{tag} small") };
        var large = await factory.WorldAsync(owner) with { SupplierId = await owner.CreateSupplierAsync(name: $"{tag} large") };
        await small.PayAsync(100);
        await large.PayAsync(900_000);

        var list = await (await owner.GetAsync($"{Debts}?search={tag}")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(
            [-900_000m, -100m],
            list.GetProperty("page").GetProperty("items").EnumerateArray().Select(r => r.Money("debt")).ToList());
    }

    // Client list §5: "A wrong period gives no file on the export", with a supplier or without, and with the same answer.
    [Fact]
    public async Task A_wrong_period_gives_no_file_also_on_the_export_without_a_supplier()
    {
        var world = await factory.WorldAsync();

        foreach (var (period, field) in new[]
        {
            ($"from={Today:yyyy-MM-dd}&to={Today.AddDays(-1):yyyy-MM-dd}", "From date"),
            ($"from={Today.AddYears(-6):yyyy-MM-dd}&to={Today:yyyy-MM-dd}", "To date"),
        })
        {
            Assert.Equal(field, await (await world.Owner.GetAsync($"{Debts}/export?{period}")).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
            Assert.Equal(field, await (await world.Owner.GetAsync($"{Debts}/export?supplierId={world.SupplierId}&{period}")).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        }
    }

    // FINDING L5, goes away with P4. BR-PURCHASE-04: the unpaid receipts show what is owed. Until P4 a return can pass its receipt (1,500 returns as 2,000);
    // SupplierDebtLedger.UnpaidReceipts counts that excess as credit only for receipts after it, so older ones show more than the debt.
    [Fact(Skip = "Goes away with P4: returns become paid-back payments")]
    public async Task Finding_L5_the_unpaid_receipts_do_not_add_up_to_more_than_the_debt_when_a_later_return_passes_its_receipt()
    {
        var world = await factory.WorldAsync();
        await world.Owner.CreateReceiptAsync(Dated(world, Today.AddDays(-9), 1_000));
        var later = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 1_500)) with { ReceiptDate = Today.AddDays(-3), Complete = true });
        await (await world.Owner.PostReturnAsync(later.Id(), new ReturnBody
        {
            DebtReductionAmount = 2_000, Lines = [new ReturnLineBody(later.LineOf(world.Other), 1)],
        })).ReadAsync(HttpStatusCode.Created);

        var detail = await (await world.Owner.GetAsync($"{Debts}/{world.SupplierId}")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(500m, detail.Money("debtToday"));
        Assert.Equal(500m, detail.GetProperty("unpaidReceipts").EnumerateArray().Sum(r => r.Money("remaining")));
    }

    // PURCHASE-05.1/05.4: each flag answers its own question only, and neither skips a field check or the other question.
    [Fact]
    public async Task Each_confirmation_flag_answers_only_its_own_question_and_no_flag_skips_a_field_check()
    {
        var world = await factory.WorldAsync();
        await world.CompletedAsync(new LineBody(world.Other, 1, 1_000));
        var reference = $"FT-{Guid.NewGuid():N}";
        await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Amount = 1, Method = "BankTransfer", BankReference = reference })).ReadAsync(HttpStatusCode.Created);
        var body = new PaymentBody { SupplierId = world.SupplierId, Amount = 5_000, Method = "BankTransfer", BankReference = reference };

        await (await world.Owner.PostPaymentAsync(body)).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-05");
        await (await world.Owner.PostPaymentAsync(body with { ConfirmDuplicateBankReference = true })).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-05");
        await (await world.Owner.PostPaymentAsync(body with { ConfirmAdvance = true })).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-06");

        var both = body with { ConfirmAdvance = true, ConfirmDuplicateBankReference = true };
        foreach (var (invalid, field) in new (PaymentBody, string)[]
        {
            (both with { Amount = 0 }, "Amount"),
            (both with { Amount = -5_000 }, "Amount"),
            (both with { Amount = 5_000.5m }, "Amount"),
            (both with { PaymentDate = Today.AddDays(1) }, "Payment date"),
            (both with { BankReference = " " }, "Bank reference"),
            (both with { Method = "Other" }, "Method"),
            (both with { SupplierId = Guid.NewGuid() }, "Supplier"),
        })
        {
            Assert.Equal(field, await (await world.Owner.PostPaymentAsync(invalid)).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        }

        var saved = await (await world.Owner.PostPaymentAsync(both)).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(1_000m - 1m - 5_000m, saved.Money("debtAfter"));

        // PURCHASE-05.2: the flag means nothing on money paid back, which is never an advance.
        var paidBack = await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Kind = "PaidBack", Amount = 7 })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(1_000m - 1m - 5_000m + 7m, paidBack.Money("debtAfter"));
    }

    [Fact]
    public async Task Amount_limits_values_that_cannot_be_read_and_a_row_version_that_cannot_be_read_answer_MSG_GEN_01()
    {
        var world = await factory.WorldAsync();
        var valid = new { supplierId = world.SupplierId, kind = "Payment", paymentDate = Today, amount = 1_000m, method = "Cash", confirmAdvance = true };

        var whole = await (await world.Owner.PostAsJsonAsync(Payments, valid)).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(1_000m, whole.Money("amount"));
        var trailingZeros = await (await world.Owner.PostAsJsonAsync(Payments, valid with { amount = 1_000.00m })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(1_000m, trailingZeros.Money("amount"));
        await (await world.Owner.PostAsJsonAsync(Payments, valid with { amount = 0.01m })).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");

        // An unreadable value names the field "request", on every endpoint of the API (pre-existing, WaterBusinessTimeQaTests): only the code is pinned here.
        foreach (var json in new[]
        {
            "{\"amount\":\"seven\"}", "{\"amount\":1e40}", "{\"kind\":\"Gift\"}", "{\"kind\":99,\"amount\":1}", "{\"method\":\"Crypto\"}", "{\"paymentDate\":\"2026-13-40\"}", "{\"supplierId\":\"nope\"}",
        })
        {
            var response = await world.Owner.PostAsync(Payments, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
            await response.ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        }

        Assert.Equal("RowVersion", await (await world.Owner.PostAsJsonAsync($"{Payments}/{whole.Id()}/cancel", new { reason = "x", rowVersion = "%%%" })).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Reason", await (await world.Owner.PostAsJsonAsync($"{Payments}/{whole.Id()}/cancel", new { })).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    // §I.4.4 PURCHASE-04/05: Owner and Accountant Full, Zone Manager No, on every endpoint and at any scope. The removed routes are gone.
    [Fact]
    public async Task Every_endpoint_follows_the_matrix_for_the_owner_the_accountant_and_the_zone_manager_and_the_removed_routes_are_gone()
    {
        var world = await factory.WorldAsync();
        var payment = await world.PayAsync(1_000);
        var (otherZone, _) = await factory.ZoneWithRaftAsync();

        IEnumerable<(string Method, string Url, object? Body)> Endpoints(Guid paymentId) =>
        [
            ("GET", Debts, null),
            ("GET", $"{Debts}/export", null),
            ("GET", $"{Debts}/export?supplierId={world.SupplierId}", null),
            ("GET", $"{Debts}/{world.SupplierId}", null),
            ("GET", $"{Debts}/{world.SupplierId}/statement", null),
            ("GET", Payments, null),
            ("GET", $"{Payments}/{paymentId}", null),
            ("POST", Payments, new PaymentBody { SupplierId = world.SupplierId, Amount = 1, ConfirmAdvance = true }),
            ("POST", $"{Payments}/{paymentId}/cancel", new { reason = "Mistake" }),
        ];

        static Task<HttpResponseMessage> Call(HttpClient client, (string Method, string Url, object? Body) e) =>
            e.Method == "GET" ? client.GetAsync(e.Url) : client.PostAsJsonAsync(e.Url, e.Body);

        var refused = new[] { await factory.ZoneManagerAsync(world.Zone), await factory.PurchasingStaffAsync(null, StaffRole.ZoneManager) };
        foreach (var client in refused)
        {
            foreach (var endpoint in Endpoints(payment.Id()))
            {
                await (await Call(client, endpoint)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
            }
        }

        var anonymous = factory.CreateClient();
        foreach (var endpoint in Endpoints(payment.Id()))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Call(anonymous, endpoint)).StatusCode);
        }

        // A pair that is no longer Effective opens nothing, also for a session opened before.
        var formerPassword = NewPassword();
        var former = await factory.AddAccountAsync(NewCode(), formerPassword, SubjectType.Staff);
        var formerPair = await factory.GrantAsync(former, StaffRole.Accountant, null);
        var formerClient = await factory.LoginAsync(former.Username, formerPassword);
        await (await formerClient.GetAsync(Debts)).ReadAsync(HttpStatusCode.OK);
        await factory.UpdateAsync<AccessGrant>(formerPair.Id, g =>
        {
            g.StatusId = StatusIds.AccessGrant.Revoked;
            g.RevokedAt = DateTimeOffset.UtcNow;
        });
        Assert.Contains((await formerClient.GetAsync(Debts)).StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized });

        foreach (var client in new[] { world.Owner, await factory.AccountantAsync(), await factory.AccountantAsync(otherZone) })
        {
            var own = await world.PayAsync(1_000);
            foreach (var endpoint in Endpoints(own.Id()))
            {
                var response = await Call(client, endpoint);
                Assert.True(response.IsSuccessStatusCode, $"{endpoint.Method} {endpoint.Url}: {(int)response.StatusCode}");
            }
        }

        Assert.Equal(HttpStatusCode.NotFound, (await world.Owner.GetAsync("/api/suppliers/debts")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.Owner.GetAsync($"/api/suppliers/{world.SupplierId}/debt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.Owner.PostAsJsonAsync($"{Payments}/{payment.Id()}/allocate", new { receiptId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.Owner.GetAsync($"{Debts}/{world.SupplierId}/unpaid-receipts")).StatusCode);
    }

    // PM 03/11: the Accountant views suppliers, and only views them.
    [Fact]
    public async Task The_accountant_views_suppliers_and_cannot_change_them()
    {
        var world = await factory.WorldAsync();
        var accountant = await factory.AccountantAsync();

        await (await accountant.GetAsync("/api/suppliers")).ReadAsync(HttpStatusCode.OK);
        await (await accountant.GetAsync($"/api/suppliers/{world.SupplierId}")).ReadAsync(HttpStatusCode.OK);
        await (await accountant.PostAsJsonAsync("/api/suppliers", new { name = "New", supplierType = "Seed", phoneNumber = NewPhone() })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await accountant.PutAsJsonAsync($"/api/suppliers/{world.SupplierId}", new { name = "New", supplierType = "Seed", phoneNumber = NewPhone() })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await accountant.PostAsync($"/api/suppliers/{world.SupplierId}/discontinue", null)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
    }

    // BR-PURCHASE-08: "not later than today", and today is Vietnam time. The day turns at 17:00 UTC.
    [Fact]
    public async Task The_payment_date_the_debt_age_and_the_default_period_turn_over_at_midnight_in_Vietnam()
    {
        var world = await factory.WorldAsync();
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        await using var timed = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(clock);
        }));
        var client = timed.CreateClient();
        client.DefaultRequestHeaders.Authorization = world.Owner.DefaultRequestHeaders.Authorization;

        var day = Today.AddDays(-3);
        var lastSecond = new DateTimeOffset(day.ToDateTime(new TimeOnly(23, 59, 59)), TimeSpan.FromHours(7));
        var body = new PaymentBody { SupplierId = world.SupplierId, Amount = 10_000 };

        clock.Now = lastSecond;
        Assert.Equal("Payment date", await (await client.PostPaymentAsync(body with { PaymentDate = day.AddDays(1) })).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        await (await client.PostReceiptAsync(Dated(world, day.AddDays(1), 100_000))).ReadAsync(HttpStatusCode.BadRequest);
        await client.CreateReceiptAsync(Dated(world, day, 100_000));
        await (await client.PostPaymentAsync(body with { PaymentDate = day })).ReadAsync(HttpStatusCode.Created);
        var before = await (await client.GetAsync($"{Debts}/{world.SupplierId}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((90_000m, 0), (before.Money("debtToday"), before.GetProperty("debtAgeDays").GetInt32()));

        clock.Now = lastSecond.AddSeconds(1);
        var after = await (await client.GetAsync($"{Debts}/{world.SupplierId}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((90_000m, 1), (after.Money("debtToday"), after.GetProperty("debtAgeDays").GetInt32()));
        await (await client.PostPaymentAsync(body with { Amount = 20_000, PaymentDate = day.AddDays(1) })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal("Payment date", await (await client.PostPaymentAsync(body with { PaymentDate = day.AddDays(2) })).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        var firstDay = await (await client.GetAsync($"{Debts}/{world.SupplierId}/statement?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}")).ReadAsync(HttpStatusCode.OK);
        var secondDay = await (await client.GetAsync($"{Debts}/{world.SupplierId}/statement?from={day.AddDays(1):yyyy-MM-dd}&to={day.AddDays(1):yyyy-MM-dd}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((0m, 90_000m, 90_000m, 70_000m), (firstDay.Money("openingDebt"), firstDay.Money("closingDebt"), secondDay.Money("openingDebt"), secondDay.Money("closingDebt")));

        // The last second of a month in Vietnam is still that month, whatever the UTC date.
        var endOfOctober = new DateTimeOffset(2026, 10, 31, 23, 59, 59, TimeSpan.FromHours(7));
        clock.Now = endOfOctober;
        var october = await (await client.GetAsync($"{Debts}/{world.SupplierId}/statement")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("2026-10-01", "2026-10-31"), (october.GetProperty("from").GetString(), october.GetProperty("to").GetString()));
        clock.Now = endOfOctober.AddSeconds(1);
        var november = await (await client.GetAsync($"{Debts}/{world.SupplierId}/statement")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("2026-11-01", "2026-11-30"), (november.GetProperty("from").GetString(), november.GetProperty("to").GetString()));
    }

    // PURCHASE-04 step 3: a document on the first and on the last day is in the period; the day before opens it; the day after is out.
    [Fact]
    public async Task The_statement_period_includes_both_ends_and_a_filter_with_only_one_end_follows_the_client_list()
    {
        var world = await factory.WorldAsync();
        var (from, to) = (Today.AddDays(-10), Today.AddDays(-5));
        await world.Owner.CreateReceiptAsync(Dated(world, from.AddDays(-1), 1));
        await world.Owner.CreateReceiptAsync(Dated(world, from, 10));
        await world.PayAsync(1, from);
        await world.PayAsync(2, to, "PaidBack");
        await world.Owner.CreateReceiptAsync(Dated(world, to, 100));
        await world.Owner.CreateReceiptAsync(Dated(world, to.AddDays(1), 1_000));
        await world.PayAsync(3, to.AddDays(1));

        var statement = await (await world.Owner.GetAsync($"{Debts}/{world.SupplierId}/statement?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(1m, statement.Money("openingDebt"));
        Assert.Equal(
            [(from, "Receipt", 10m), (from, "Payment", -1m), (to, "Receipt", 100m), (to, "PaidBack", 2m)],
            statement.GetProperty("entries").EnumerateArray().Select(e => (DateOnly.Parse(e.GetProperty("date").GetString()!), e.GetProperty("kind").GetString(), e.Money("amount"))).ToList());
        Assert.Equal((1m + 10 - 1 + 100 + 2, 1m + 10 - 1 + 100 + 2 + 1_000 - 3), (statement.Money("closingDebt"), statement.Money("debtToday")));

        var month = new DateOnly(to.Year, to.Month, 1);
        var onlyEnd = await (await world.Owner.GetAsync($"{Debts}/{world.SupplierId}/statement?to={to:yyyy-MM-dd}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(month.ToString("yyyy-MM-dd"), onlyEnd.GetProperty("from").GetString());
        var onlyStart = await (await world.Owner.GetAsync($"{Debts}/{world.SupplierId}/statement?from={from:yyyy-MM-dd}")).ReadAsync(HttpStatusCode.OK);
        var thisMonth = new DateOnly(Today.Year, Today.Month, 1);
        Assert.Equal(thisMonth.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd"), onlyStart.GetProperty("to").GetString());
        Assert.Equal(onlyStart.Money("debtToday"), onlyStart.Money("closingDebt"));
    }

    // Text of the supplier and of the payment is never read as a formula, and a control character does not lose the file.
    [Fact]
    public async Task Supplier_and_payment_text_in_the_export_stays_text_and_a_control_character_does_not_lose_the_file()
    {
        var tag = NewCode();
        var world = await factory.WorldAsync();
        var name = $"=HYPERLINK(\"http://evil\",\"{tag}\")";
        world = world with { SupplierId = await world.Owner.CreateSupplierAsync(name: name) };
        await (await world.Owner.PostPaymentAsync(new PaymentBody
        {
            SupplierId = world.SupplierId, Kind = "PaidBack", Amount = 5, Method = "BankTransfer",
            BankReference = $"=cmd|'/c calc'!A{tag}", Note = "@SUM(1+1)\u0001 +1",
        })).ReadAsync(HttpStatusCode.Created);

        var export = await world.Owner.GetAsync($"{Debts}/export?supplierId={world.SupplierId}&search={tag}");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);

        using var workbook = new XLWorkbook(await export.Content.ReadAsStreamAsync());
        var list = workbook.Worksheet("Supplier debts").Cell(2, 1);
        Assert.Equal((false, name), (list.HasFormula, list.GetString()));
        var entry = workbook.Worksheet("Statement").RowsUsed().First(r => r.Cell(2).GetString() == "Money paid back");
        Assert.False(entry.Cell(6).HasFormula);
        Assert.StartsWith("=cmd", entry.Cell(6).GetString());
        Assert.False(entry.Cell(7).HasFormula);
        Assert.StartsWith("@SUM", entry.Cell(7).GetString());
    }

    // BR-PURCHASE-05: every active Owner, not the Accountant or a Zone Manager; the parameters are atomic; a locked Owner is left out.
    [Fact]
    public async Task A_transaction_goes_to_every_active_owner_only_with_atomic_parameters_and_the_type_is_offered_as_mandatory()
    {
        var world = await factory.WorldAsync();
        var password = NewPassword();
        var second = await factory.AddAccountAsync(NewCode(), password, SubjectType.CooperativeOwner);
        var locked = await factory.AddAccountAsync(NewCode(), password, SubjectType.CooperativeOwner, a => a.StatusId = StatusIds.Account.Locked);
        var accountantAccount = await factory.AddAccountAsync(NewCode(), password, SubjectType.Staff);
        await factory.GrantAsync(accountantAccount, StaffRole.Accountant, null);
        var managerAccount = await factory.AddAccountAsync(NewCode(), password, SubjectType.Staff);
        await factory.GrantAsync(managerAccount, StaffRole.ZoneManager, world.Zone);
        var accountant = await factory.LoginAsync(accountantAccount.Username, password);
        await world.CompletedAsync(new LineBody(world.Other, 1, 1_000));

        var payment = await (await accountant.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Amount = 400 })).ReadAsync(HttpStatusCode.Created);
        await (await accountant.CancelPaymentAsync(payment.Id(), "Mistake")).ReadAsync(HttpStatusCode.OK);

        var rows = await factory.QueryAsync(db => db.Notifications.Where(n => n.RelatedRecordId == payment.Id()).ToListAsync());
        Assert.Equal(2, rows.Count(n => n.RecipientAccountId == second.Id));
        Assert.DoesNotContain(rows, n => n.RecipientAccountId == locked.Id || n.RecipientAccountId == accountantAccount.Id || n.RecipientAccountId == managerAccount.Id);
        Assert.All(rows, n => Assert.Equal((NotificationTypes.SupplierTransaction, "MSG-PURCHASE-04", true), (n.Type, n.MessageCode, n.IsMandatory)));

        var pushes = factory.Services.GetRequiredService<FakePushSender>().Inbox.List(second.Id.ToString())
            .Where(p => p.MessageParams is { } values && values.TryGetProperty("payment_code", out var code) && code.GetString() == payment.GetProperty("code").GetString()).ToList();
        Assert.Equal(2, pushes.Count);

        var parameters = rows.Where(n => n.RecipientAccountId == second.Id).Select(n => JsonDocument.Parse(n.MessageParams!).RootElement).ToList();
        foreach (var p in parameters)
        {
            Assert.Equal(
                ["amount", "debt_amount", "payment_code", "supplier_name", "transaction_kind", "user_name"],
                p.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
            Assert.Equal((JsonValueKind.Number, JsonValueKind.Number), (p.GetProperty("amount").ValueKind, p.GetProperty("debt_amount").ValueKind));
        }

        Assert.Equal(
            [("Payment", 600m), ("PaymentCancelled", 1_000m)],
            parameters.Select(p => (p.GetProperty("transaction_kind").GetString(), p.GetProperty("debt_amount").GetDecimal())).OrderBy(x => x.Item1, StringComparer.Ordinal).ToList());

        var preferences = await (await world.Owner.GetAsync("/api/me/notification-preferences")).ReadAsync(HttpStatusCode.OK);
        var offered = preferences.GetProperty("types").EnumerateArray().Single(i => i.GetProperty("type").GetString() == NotificationTypes.SupplierTransaction);
        Assert.True(offered.GetProperty("mandatory").GetBoolean() && offered.GetProperty("push").GetBoolean());
        var ofAccountant = await (await accountant.GetAsync("/api/me/notification-preferences")).ReadAsync(HttpStatusCode.OK);
        Assert.DoesNotContain(ofAccountant.GetProperty("types").EnumerateArray(), i => i.GetProperty("type").GetString() == NotificationTypes.SupplierTransaction);
    }

    private sealed class SettableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
