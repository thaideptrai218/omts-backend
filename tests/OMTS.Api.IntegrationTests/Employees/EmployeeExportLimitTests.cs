using System.Net;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Identity;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>EMP-API-03: an export of more than 5000 employees is refused with a hint to narrow the filter. Own class: it adds thousands of rows.</summary>
public class EmployeeExportLimitTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task An_export_of_5000_employees_is_written_and_one_of_5001_is_refused_with_a_hint()
    {
        var (owner, _, _) = await SetupAsync(factory);
        var cooperativeId = await factory.QueryAsync(db => db.Cooperatives.Select(c => c.Id).FirstAsync());
        await factory.UseDbAsync(db => db.Employees.AddRange(
            Enumerable.Range(0, 5000).Select(i => new Employee { CooperativeId = cooperativeId, FullName = $"Bulk {i:0000}" })));

        var exactly = await owner.GetAsync("/api/employees/export?search=Bulk");
        Assert.Equal(HttpStatusCode.OK, exactly.StatusCode);
        using (var workbook = new XLWorkbook(new MemoryStream(await exactly.Content.ReadAsByteArrayAsync())))
        {
            Assert.Equal(5001, workbook.Worksheet(1).RowsUsed().Count());
        }

        await factory.UseDbAsync(db => db.Employees.Add(new Employee { CooperativeId = cooperativeId, FullName = "Bulk 5000" }));
        var tooMany = await owner.GetAsync("/api/employees/export?search=Bulk");
        var narrowed = await owner.GetAsync("/api/employees/export?search=Bulk 4999");

        var problem = await tooMany.ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        Assert.Equal("Filter", problem.GetProperty("field").GetString());
        Assert.Contains("5000", problem.GetProperty("hint").GetString());
        Assert.Equal(HttpStatusCode.OK, narrowed.StatusCode);
    }
}
