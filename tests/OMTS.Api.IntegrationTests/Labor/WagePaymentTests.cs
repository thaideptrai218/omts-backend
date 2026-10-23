using System.Net;
using System.Net.Http.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.LaborCost;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Labor.LaborTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LAB-09 wage payments: partial then full, limits, duplicates, Draft payslips, amount and date rules, bulk, reversals and advances.</summary>
public class WagePaymentTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    /// <summary>A finalized payslip of a new worker: <paramref name="days"/> paid days at 300,000.</summary>
    private static async Task<(PayrollPeriod Month, Guid PayslipId, Guid WorkerId)> FinalizedPayslipAsync(LaborWorld w, int days = 3, bool finalize = true)
    {
        var month = NewMonth();
        var worker = await w.AddWorkerAsync();
        await w.AddRateAsync(month, null, worker.Id, SalaryType.DailyWage, 300_000);
        for (var day = 1; day <= days; day++)
        {
            await w.AddAttendanceAsync(month.Day(day), null, worker.Id);
        }

        var payslip = (await w.CreatePayrollAsync(month)).PayslipOf(worker.Id).GetProperty("id").GetGuid();
        if (finalize)
        {
            await ExpectAsync(await w.Owner.PostAsync($"/api/payrolls/{month.Key}/finalize", null), HttpStatusCode.OK);
        }

        return (month, payslip, worker.Id);
    }

    private static object Payment(decimal amount, DateOnly date, string? reference = null) => new { amountPaid = amount, paymentDate = date, method = "Cash", referenceCode = reference };

    [Fact]
    public async Task Partial_then_full_payment_moves_the_payslip_to_fully_paid()
    {
        var w = await factory.WorldAsync();
        var (month, payslip, _) = await FinalizedPayslipAsync(w);

        var partial = await ExpectAsync(await w.Owner.PostAsJsonAsync($"/api/payslips/{payslip}/payments", Payment(400_000, month.End)), HttpStatusCode.Created);
        var full = await ExpectAsync(await w.Owner.PostAsJsonAsync($"/api/payslips/{payslip}/payments", Payment(500_000, month.End)), HttpStatusCode.Created);
        var unpaid = await ExpectAsync(await w.Owner.GetAsync($"/api/payslips?period={month.Key}"), HttpStatusCode.OK);

        Assert.Equal(("Approved", 500_000m), (partial.GetProperty("payslip").GetProperty("status").GetString(), partial.GetProperty("payslip").GetProperty("remaining").GetDecimal()));
        Assert.Equal(("Fully Paid", 0m), (full.GetProperty("payslip").GetProperty("status").GetString(), full.GetProperty("payslip").GetProperty("remaining").GetDecimal()));
        Assert.Equal(0, unpaid.GetArrayLength());
        Assert.Contains(await factory.AuditOfAsync(payslip), e => e.Action == "LaborSettlement.StatusChanged" && e.After!.Contains("PAID"));
    }

    [Fact]
    public async Task Amount_above_the_remaining_is_MSG45()
    {
        var w = await factory.WorldAsync();
        var (month, payslip, _) = await FinalizedPayslipAsync(w, days: 5);

        var problem = await (await w.Owner.PostAsJsonAsync($"/api/payslips/{payslip}/payments", Payment(1_600_000, month.End))).ProblemAsync(HttpStatusCode.Conflict, "MSG45");

        Assert.Equal("Amount paid exceeds the allowed limit (1,500,000).", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Same_payslip_amount_date_and_reference_is_MSG03()
    {
        var w = await factory.WorldAsync();
        var (month, payslip, _) = await FinalizedPayslipAsync(w);
        var first = await ExpectAsync(await w.Owner.PostAsJsonAsync($"/api/payslips/{payslip}/payments", Payment(100_000, month.End, "PC-01")), HttpStatusCode.Created);

        var problem = await (await w.Owner.PostAsJsonAsync($"/api/payslips/{payslip}/payments", Payment(100_000, month.End, "pc-01"))).ProblemAsync(HttpStatusCode.Conflict, "MSG03");

        Assert.StartsWith("Payment already exists.", problem.GetProperty("detail").GetString());
        Assert.Equal(first.GetProperty("payment").GetProperty("id").GetGuid(), problem.GetProperty("existingId").GetGuid());
    }

    [Fact]
    public async Task Draft_payslip_cannot_be_paid_MSG61()
    {
        var w = await factory.WorldAsync();
        var (month, payslip, _) = await FinalizedPayslipAsync(w, finalize: false);

        var problem = await (await w.Owner.PostAsJsonAsync($"/api/payslips/{payslip}/payments", Payment(100_000, month.End))).ProblemAsync(HttpStatusCode.Conflict, "MSG61");

        Assert.Equal("Cannot change Payslip from “Draft” to “Fully Paid”: the payroll is not finalized.", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Amount_not_in_thousands_is_MSG41_and_a_future_date_is_MSG01()
    {
        var w = await factory.WorldAsync();
        var (month, payslip, _) = await FinalizedPayslipAsync(w);
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)).AddDays(1);

        await (await w.Owner.PostAsJsonAsync($"/api/payslips/{payslip}/payments", Payment(100_500, month.End))).ProblemAsync(HttpStatusCode.BadRequest, "MSG41");
        var future = await (await w.Owner.PostAsJsonAsync($"/api/payslips/{payslip}/payments", Payment(100_000, tomorrow))).ProblemAsync(HttpStatusCode.BadRequest, "MSG01");

        Assert.Equal("Payment date", future.Field());
    }

    [Fact]
    public async Task Bulk_payment_pays_every_remaining_or_nothing()
    {
        var w = await factory.WorldAsync();
        var (month, first, _) = await FinalizedPayslipAsync(w);
        var (_, second, _) = await FinalizedPayslipAsync(w, days: 2);
        var (_, draft, _) = await FinalizedPayslipAsync(w, finalize: false);
        var body = (Guid[] ids) => new { payslipIds = ids, paymentDate = month.End, method = "BankTransfer" };

        var refused = await (await w.Owner.PostAsJsonAsync("/api/payslips/payments/bulk", body([first, draft, second]))).ProblemAsync(HttpStatusCode.Conflict, "MSG61");
        var stillUnpaid = await ExpectAsync(await w.Owner.GetAsync($"/api/payslips/{first}"), HttpStatusCode.OK);
        var paid = await ExpectAsync(await w.Owner.PostAsJsonAsync("/api/payslips/payments/bulk", body([first, second])), HttpStatusCode.Created);

        Assert.Equal(draft, Assert.Single(refused.GetProperty("failures").EnumerateArray()).GetProperty("payslipId").GetGuid());
        Assert.Equal(0m, stillUnpaid.GetProperty("paid").GetDecimal());
        Assert.All(paid.EnumerateArray(), p => Assert.Equal("Fully Paid", p.GetProperty("payslip").GetProperty("status").GetString()));
        Assert.Equal([900_000m, 600_000m], paid.EnumerateArray().Select(p => p.GetProperty("payment").GetProperty("amountPaid").GetDecimal()));
    }

    [Fact]
    public async Task Reversal_needs_a_reason_and_is_refused_once_the_payslip_is_fully_paid()
    {
        var w = await factory.WorldAsync();
        var (month, payslip, _) = await FinalizedPayslipAsync(w);
        var wrong = await ExpectAsync(await w.Owner.PostAsJsonAsync($"/api/payslips/{payslip}/payments", Payment(200_000, month.End)), HttpStatusCode.Created);
        var wrongId = wrong.GetProperty("payment").GetProperty("id").GetGuid();

        var noReason = await (await w.Owner.PostAsJsonAsync($"/api/labor-payments/{wrongId}/reverse", new { reason = "" })).ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        var reversed = await ExpectAsync(await w.Owner.PostAsJsonAsync($"/api/labor-payments/{wrongId}/reverse", new { reason = "Nhập nhầm số tiền" }), HttpStatusCode.Created);
        var twice = await (await w.Owner.PostAsJsonAsync($"/api/labor-payments/{wrongId}/reverse", new { reason = "x" })).ProblemAsync(HttpStatusCode.Conflict, "MSG61");
        var full = await ExpectAsync(await w.Owner.PostAsJsonAsync($"/api/payslips/{payslip}/payments", Payment(900_000, month.End)), HttpStatusCode.Created);
        var final = await (await w.Owner.PostAsJsonAsync($"/api/labor-payments/{full.GetProperty("payment").GetProperty("id").GetGuid()}/reverse", new { reason = "x" }))
            .ProblemAsync(HttpStatusCode.Conflict, "MSG61");

        Assert.Equal("Reason", noReason.Field());
        Assert.Equal((-200_000m, 0m), (reversed.GetProperty("payment").GetProperty("amountPaid").GetDecimal(), reversed.GetProperty("payslip").GetProperty("paid").GetDecimal()));
        Assert.Contains("already been reversed", twice.GetProperty("detail").GetString());
        Assert.Equal("Cannot change Payslip from “Fully Paid” to “Approved”: Fully Paid is final.", final.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Receipt_or_signature_photos_are_added_to_a_payment_up_to_five()
    {
        var w = await factory.WorldAsync();
        var (month, payslip, _) = await FinalizedPayslipAsync(w);
        var payment = (await ExpectAsync(await w.Owner.PostAsJsonAsync($"/api/payslips/{payslip}/payments", Payment(100_000, month.End)), HttpStatusCode.Created))
            .GetProperty("payment").GetProperty("id").GetGuid();
        MultipartFormDataContent Photos(int count, byte[] content)
        {
            var form = new MultipartFormDataContent();
            for (var i = 0; i < count; i++)
            {
                var file = new ByteArrayContent(content);
                file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                form.Add(file, "files", $"receipt{i}.png");
            }

            return form;
        }

        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D];
        var saved = await ExpectAsync(await w.Owner.PostAsync($"/api/labor-payments/{payment}/documents", Photos(5, png)), HttpStatusCode.OK);
        var sixth = await (await w.Owner.PostAsync($"/api/labor-payments/{payment}/documents", Photos(1, png))).ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        var notImage = await (await w.Owner.PostAsync($"/api/labor-payments/{payment}/documents", Photos(1, "%PDF-1.4"u8.ToArray()))).ProblemAsync(HttpStatusCode.BadRequest, "MSG01");

        Assert.Equal(5, saved.GetProperty("documentUrls").GetArrayLength());
        Assert.Equal(("Evidence", "Evidence"), (sixth.Field(), notImage.Field()));
    }

    [Fact]
    public async Task Advance_is_reversible_until_a_finalized_payroll_deducts_it()
    {
        var w = await factory.WorldAsync();
        var month = NewMonth();
        var worker = await w.AddWorkerAsync();
        await w.AddRateAsync(month, null, worker.Id, SalaryType.DailyWage, 300_000);
        await w.AddAttendanceAsync(month.Day(1), null, worker.Id);
        var mistaken = await ExpectAsync(await w.Owner.PostAsJsonAsync("/api/salary-advances",
            new { workerId = worker.Id, amount = 50_000, paymentDate = month.Day(1), method = "Cash" }), HttpStatusCode.Created);
        var kept = await ExpectAsync(await w.Owner.PostAsJsonAsync("/api/salary-advances",
            new { workerId = worker.Id, amount = 100_000, paymentDate = month.Day(2), method = "Cash", notes = "Ứng lương" }), HttpStatusCode.Created);

        await ExpectAsync(await w.Owner.PostAsJsonAsync($"/api/labor-payments/{mistaken.GetProperty("id").GetGuid()}/reverse", new { reason = "Ghi nhầm" }), HttpStatusCode.Created);
        var slip = (await w.CreatePayrollAsync(month)).PayslipOf(worker.Id);
        await ExpectAsync(await w.Owner.PostAsync($"/api/payrolls/{month.Key}/finalize", null), HttpStatusCode.OK);
        var deducted = await (await w.Owner.PostAsJsonAsync($"/api/labor-payments/{kept.GetProperty("id").GetGuid()}/reverse", new { reason = "x" }))
            .ProblemAsync(HttpStatusCode.Conflict, "MSG61");
        var odd = await (await w.Owner.PostAsJsonAsync("/api/salary-advances", new { workerId = worker.Id, amount = 10_500, paymentDate = month.Day(3), method = "Cash" }))
            .ProblemAsync(HttpStatusCode.BadRequest, "MSG41");

        Assert.Equal((100_000m, 200_000m), (slip.GetProperty("advanceDeduction").GetDecimal(), slip.GetProperty("net").GetDecimal()));
        Assert.Equal("Cannot change Salary advance from “Deducted” to “Reversed”: it has already been deducted by a finalized payroll.", deducted.GetProperty("detail").GetString());
        Assert.Equal("The amount must be a multiple of 1,000 VND.", odd.GetProperty("detail").GetString());
    }
}
