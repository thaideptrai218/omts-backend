using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Labor.LaborTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LAB-06 pay rates: automatic closing, overlaps, amount rules, edits of used versions, bulk all-or-nothing and access.</summary>
public class PayRateTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string PayRates = "/api/pay-rates";

    [Fact]
    public async Task Rate_is_saved_as_approved_and_a_later_version_closes_the_open_ended_one()
    {
        var w = await factory.WorldAsync();
        var employee = await w.AddEmployeeAsync();

        var first = await ExpectAsync(await w.Owner.PostAsJsonAsync(PayRates, new
        {
            employeeId = employee.Id, salaryType = "PerWorkingDay", amount = 8_000_000, standardWorkingDays = 26, validFrom = "2040-01-01",
            components = new[] { new { kind = "Allowance", name = "Lunch", amount = 30_000, basis = "PerPaidDay" } },
        }), HttpStatusCode.Created);
        var second = await ExpectAsync(await w.Owner.PostAsJsonAsync(PayRates, new
        {
            employeeId = employee.Id, salaryType = "MonthlyFixed", amount = 9_000_000, validFrom = "2040-07-01",
        }), HttpStatusCode.Created);

        var firstId = first.GetProperty("rate").GetProperty("id").GetGuid();
        Assert.Equal("Approved", first.GetProperty("rate").GetProperty("status").GetString());
        Assert.Equal("Lunch", first.GetProperty("rate").GetProperty("components")[0].GetProperty("name").GetString());
        Assert.Equal((firstId, "2040-06-30"), (second.GetProperty("closedPrevious").GetProperty("id").GetGuid(), second.GetProperty("closedPrevious").GetProperty("validTo").GetString()));
        Assert.Contains(await factory.AuditOfAsync(firstId), e => e.Action == "PayRuleVersion.Closed");
        var history = await ExpectAsync(await w.Owner.GetAsync($"{PayRates}?employeeId={employee.Id}"), HttpStatusCode.OK);
        Assert.Equal(2, history.GetArrayLength());
    }

    [Fact]
    public async Task Any_other_overlap_is_MSG03_with_the_existing_version()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync();
        var closed = await ExpectAsync(await w.Owner.PostAsJsonAsync(PayRates, new
        {
            workerId = worker.Id, salaryType = "DailyWage", amount = 300_000, validFrom = "2040-01-01", validTo = "2040-06-30",
        }), HttpStatusCode.Created);

        var problem = await (await w.Owner.PostAsJsonAsync(PayRates, new { workerId = worker.Id, salaryType = "DailyWage", amount = 320_000, validFrom = "2040-06-01" }))
            .ProblemAsync(HttpStatusCode.Conflict, "MSG03");

        Assert.StartsWith("Pay rate already exists.", problem.GetProperty("detail").GetString());
        Assert.Equal(closed.GetProperty("rate").GetProperty("id").GetGuid(), problem.GetProperty("existingId").GetGuid());
    }

    [Fact]
    public async Task Amount_not_in_thousands_is_MSG41_worker_salary_type_and_missing_standard_days_are_MSG01()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync();
        var employee = await w.AddEmployeeAsync();

        var odd = await (await w.Owner.PostAsJsonAsync(PayRates, new { workerId = worker.Id, salaryType = "DailyWage", amount = 300_500, validFrom = "2040-01-01" }))
            .ProblemAsync(HttpStatusCode.BadRequest, "MSG41");
        var monthly = await (await w.Owner.PostAsJsonAsync(PayRates, new { workerId = worker.Id, salaryType = "MonthlyFixed", amount = 5_000_000, validFrom = "2040-01-01" }))
            .ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        var noDays = await (await w.Owner.PostAsJsonAsync(PayRates, new { employeeId = employee.Id, salaryType = "PerWorkingDay", amount = 5_000_000, validFrom = "2040-01-01" }))
            .ProblemAsync(HttpStatusCode.BadRequest, "MSG01");

        Assert.Equal("The amount must be a multiple of 1,000 VND.", odd.GetProperty("detail").GetString());
        Assert.Equal("Salary type", monthly.Field());
        Assert.Equal("Standard working days", noDays.Field());
    }

    [Fact]
    public async Task Version_used_by_a_finalized_payslip_cannot_be_edited_MSG11_an_unused_one_needs_a_reason()
    {
        var w = await factory.WorldAsync();
        var employee = await w.AddEmployeeAsync();
        var month = NewMonth();
        var used = await w.AddRateAsync(month, employee.Id, null, SalaryType.MonthlyFixed, 5_000_000);
        var unused = await w.AddRateAsync(NewMonth(), employee.Id, null, SalaryType.MonthlyFixed, 5_000_000);
        await factory.UseDbAsync(db =>
        {
            var payroll = new Payroll { Period = month.Key, PeriodStart = month.Start, PeriodEnd = month.End, PreparedByAccountId = w.OwnerAccount.Id, FinalizedAt = DateTimeOffset.UtcNow };
            var payslip = new LaborSettlement
            {
                PayrollId = payroll.Id, EmployeeId = employee.Id, PeriodStart = month.Start, PeriodEnd = month.End, TotalObligation = 5_000_000,
                PreparedByAccountId = w.OwnerAccount.Id, StatusId = StatusIds.LaborSettlement.Approved,
            };
            db.AddRange(payroll, payslip, new PayLine
            {
                SettlementId = payslip.Id, EmployeeId = employee.Id, PayRuleVersionId = used.Id, PayDate = month.End, LineType = PayLineType.MonthlySalary, Amount = 5_000_000,
            });
        });
        object Edit(PayRuleVersion v, string? reason) => new
        {
            salaryType = "MonthlyFixed", amount = 6_000_000, validFrom = v.ValidFrom, validTo = v.ValidTo, reason, rowVersion = Convert.ToBase64String(v.RowVersion),
        };

        var inUse = await (await w.Owner.PutAsJsonAsync($"{PayRates}/{used.Id}", Edit(used, "Tăng lương"))).ProblemAsync(HttpStatusCode.Conflict, "MSG11");
        var noReason = await (await w.Owner.PutAsJsonAsync($"{PayRates}/{unused.Id}", Edit(unused, null))).ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        var edited = await ExpectAsync(await w.Owner.PutAsJsonAsync($"{PayRates}/{unused.Id}", Edit(unused, "Tăng lương")), HttpStatusCode.OK);

        Assert.Equal($"Cannot proceed because 1 records are still in use: finalized payslips {month.Label}. Please handle these records first.",
            inUse.GetProperty("detail").GetString());
        Assert.Equal("Reason", noReason.Field());
        Assert.Equal(6_000_000m, edited.GetProperty("rate").GetProperty("amount").GetDecimal());
        Assert.Contains(await factory.AuditOfAsync(unused.Id), e => e.Action == "PayRuleVersion.Updated" && e.Reason == "Tăng lương");
    }

    [Fact]
    public async Task Bulk_apply_is_all_or_nothing_and_lists_every_failure()
    {
        var w = await factory.WorldAsync();
        var first = await w.AddWorkerAsync();
        var second = await w.AddWorkerAsync();
        var discontinued = await w.AddWorkerAsync(configure: x => x.StatusId = StatusIds.Usage.Discontinued);
        var rate = new { salaryType = "DailyWage", amount = 310_000, validFrom = "2040-03-01" };

        var refused = await (await w.Owner.PostAsJsonAsync($"{PayRates}/bulk", new
        {
            people = new[] { new { workerId = (Guid?)first.Id }, new { workerId = (Guid?)discontinued.Id }, new { workerId = (Guid?)second.Id } }, rate,
        })).ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        var savedAfterRefusal = await factory.QueryAsync(db => db.PayRuleVersions.CountAsync(v => v.WorkerId == first.Id || v.WorkerId == second.Id));
        var created = await ExpectAsync(await w.Owner.PostAsJsonAsync($"{PayRates}/bulk", new
        {
            people = new[] { new { workerId = (Guid?)first.Id }, new { workerId = (Guid?)second.Id } }, rate,
        }), HttpStatusCode.Created);

        var failure = Assert.Single(refused.GetProperty("failures").EnumerateArray());
        Assert.Equal((1, discontinued.Id, "Worker"), (failure.GetProperty("index").GetInt32(), failure.GetProperty("id").GetGuid(), failure.GetProperty("field").GetString()));
        Assert.Equal(0, savedAfterRefusal);
        Assert.Equal(2, created.GetArrayLength());
    }

    [Fact]
    public async Task People_list_shows_current_rates_without_personal_data_and_needs_pay_rules_at_all_zones()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync(configure: x => x.CitizenId = "098765432");
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        await w.AddRateAsync(new(today.Year, today.Month), null, worker.Id, SalaryType.DailyWage, 300_000);
        var (accountant, _) = await factory.StaffAsync(null, (PermissionFunctions.PayRules, true, true, true, false));
        var (zoneOnly, _) = await factory.StaffAsync(w.Zone, (PermissionFunctions.PayRules, true, true, true, false));

        var response = await accountant.GetAsync($"{PayRates}/people?search={Uri.EscapeDataString(worker.FullName)}");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("300000", text);
        Assert.DoesNotContain(worker.PhoneNumber!, text);
        Assert.DoesNotContain("098765432", text);
        await (await zoneOnly.GetAsync($"{PayRates}/people")).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
    }
}
