using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Sales;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Api.IntegrationTests.Dashboard;

/// <summary>FARM-06 calls, the reading of an answer, and the records its tiles are made of.</summary>
public static class OperationsDashboardTestKit
{
    public static readonly DateOnly Today = PurchaseRequestTestKit.Today;

    public static Task<HttpResponseMessage> GetDashboardAsync(this HttpClient client, string query = "") => client.GetAsync("/api/dashboard" + query);

    public static async Task<JsonElement> DashboardAsync(this HttpClient client, string query = "") =>
        await (await client.GetDashboardAsync(query)).ReadAsync(HttpStatusCode.OK);

    public static decimal RevenueTotal(this JsonElement dashboard) => dashboard.GetProperty("dailyRevenue").GetProperty("total").GetDecimal();

    public static int RevenueCount(this JsonElement dashboard) => dashboard.GetProperty("dailyRevenue").GetProperty("invoiceCount").GetInt32();

    public static decimal Receivables(this JsonElement dashboard) => dashboard.GetProperty("receivables").GetProperty("receivables").GetDecimal();

    public static decimal OwedBack(this JsonElement dashboard) => dashboard.GetProperty("receivables").GetProperty("owedBack").GetDecimal();

    public static int OpenLots(this JsonElement dashboard) => dashboard.GetProperty("openHarvestLots").GetProperty("count").GetInt32();

    public static int WaterAlerts(this JsonElement dashboard) => dashboard.GetProperty("waterAlerts").GetProperty("count").GetInt32();

    public static List<Guid> AlertZoneIds(this JsonElement dashboard) =>
        dashboard.GetProperty("waterAlerts").GetProperty("zones").EnumerateArray().Select(z => z.GetProperty("zoneId").GetGuid()).ToList();

    public static int PendingTotal(this JsonElement dashboard) => dashboard.GetProperty("pendingTasks").GetProperty("total").GetInt32();

    public static JsonElement PendingGroup(this JsonElement dashboard, string kind) =>
        dashboard.GetProperty("pendingTasks").GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("kind").GetString() == kind);

    public static int PendingCount(this JsonElement dashboard, string kind) => dashboard.PendingGroup(kind).GetProperty("count").GetInt32();

    public static List<Guid> PendingIds(this JsonElement dashboard, string kind) =>
        dashboard.PendingGroup(kind).GetProperty("oldest").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToList();

    /// <summary>An invoice of the zone on a business day, Completed unless <paramref name="statusId"/> says otherwise.</summary>
    public static async Task<SalesInvoice> AddInvoiceAsync(
        this AuthApiFactory api, Guid zoneId, Guid accountId, decimal total, DateOnly? day = null, int statusId = StatusIds.SalesInvoice.Completed, Guid? customerId = null)
    {
        var invoice = SalesInvoiceSearchQaTests.NewInvoice(zoneId, accountId, day, total: total);
        (invoice.StatusId, invoice.CustomerId) = (statusId, customerId);
        await api.UseDbAsync(db => db.SalesInvoices.Add(invoice));
        return invoice;
    }

    public static async Task<PurchaseRequest> AddPurchaseRequestAsync(
        this AuthApiFactory api, SaleSetup sale, int statusId = StatusIds.PurchaseRequest.AwaitingResponse, TimeSpan? sentAgo = null)
    {
        var request = new PurchaseRequest
        {
            CustomerId = sale.CustomerId, ProductId = sale.ProductId, RequestedCages = 10, PickupDate = Today.AddDays(2), SentAt = DateTimeOffset.UtcNow - (sentAgo ?? TimeSpan.FromHours(1)),
            StatusId = statusId,
        };
        await api.UseDbAsync(db => db.PurchaseRequests.Add(request));
        return request;
    }

    public static async Task<QualityClaim> AddQualityClaimAsync(this AuthApiFactory api, SalesInvoice invoice, int statusId = StatusIds.QualityClaim.AwaitingResponse)
    {
        var claim = new QualityClaim
        {
            Code = "KN" + AcsTestKit.NewCode(), InvoiceId = invoice.Id, CustomerId = invoice.CustomerId!.Value, SentAt = DateTimeOffset.UtcNow.AddHours(-2), Description = "Shells open",
            StatusId = statusId,
        };
        await api.UseDbAsync(db => db.QualityClaims.Add(claim));
        return claim;
    }

    /// <summary>A payroll of one month, <paramref name="monthsAgo"/> months back, in the given status.</summary>
    /// <summary>An employee request of an employee of the cooperative (no zone), sent <paramref name="sentAgo"/> ago.</summary>
    public static async Task<EmployeeRequest> AddEmployeeRequestAsync(
        this AuthApiFactory api, int statusId, EmployeeRequestType type = EmployeeRequestType.Leave, int days = 1, TimeSpan? sentAgo = null, string? employeeName = null)
    {
        var employee = new OMTS.Domain.Entities.Identity.Employee { CooperativeId = (await api.EnsureCooperativeAsync()).Id, FullName = employeeName ?? AcsTestKit.NewCode() };
        var request = new EmployeeRequest
        {
            EmployeeId = employee.Id, Type = type, FromDate = Today, ToDate = Today.AddDays(days - 1), Reason = "Việc nhà",
            RequestedTime = type == EmployeeRequestType.Leave ? null : new TimeOnly(7, 30), SentAt = DateTimeOffset.UtcNow - (sentAgo ?? TimeSpan.Zero), StatusId = statusId, ClientOperationId = Guid.NewGuid(),
        };
        await api.UseDbAsync(db =>
        {
            db.Employees.Add(employee);
            db.EmployeeRequests.Add(request);
        });
        return request;
    }

    public static async Task<Payroll> AddPayrollAsync(this AuthApiFactory api, int statusId, int monthsAgo)
    {
        var first = new DateOnly(Today.Year, Today.Month, 1).AddMonths(-monthsAgo);
        var payroll = new Payroll { PeriodStart = first, PeriodEnd = first.AddMonths(1).AddDays(-1), StatusId = statusId };
        await api.UseDbAsync(db => db.Payrolls.Add(payroll));
        return payroll;
    }
}
