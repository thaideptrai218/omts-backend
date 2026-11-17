using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Dashboard;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>FARM-06 figures on the indexes of the tables themselves (InvoiceDate and ZoneId of the invoice, the status of each record): sums and counts in the database, read only.</summary>
internal sealed class OperationsDashboardQuery(OmtsDbContext db) : IOperationsDashboardQuery
{
    public async Task<DashboardRevenueFigures> RevenueAsync(DateOnly from, DateOnly to, Guid? zoneId, CancellationToken cancellationToken)
    {
        var invoices = db.SalesInvoices.AsNoTracking()
            .Where(i => i.InvoiceDate >= from && i.InvoiceDate <= to && i.StatusId != StatusIds.SalesInvoice.Cancelled);
        if (zoneId is { } zone)
        {
            invoices = invoices.Where(i => i.ZoneId == zone);
        }

        // No invoice sums to null, which is 0.
        var total = await invoices.SumAsync(i => (decimal?)i.Total, cancellationToken) ?? 0m;
        return new DashboardRevenueFigures(total, await invoices.CountAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<PendingTaskFigures>> PendingTasksAsync(int oldestCount, CancellationToken cancellationToken)
    {
        var requests = db.PurchaseRequests.AsNoTracking().Where(r => r.StatusId == StatusIds.PurchaseRequest.AwaitingResponse);
        var claims = db.QualityClaims.AsNoTracking().Where(c => c.StatusId == StatusIds.QualityClaim.AwaitingResponse);
        var payrolls = db.Payrolls.AsNoTracking().Where(p => p.StatusId == StatusIds.Payroll.AwaitingFinalization);
        var employeeRequests = db.EmployeeRequests.AsNoTracking().Where(r => r.StatusId == StatusIds.EmployeeRequest.AwaitingReview);
        // The label fields are read in SQL and put together here, so no formatting has to be translated.
        var oldestRequests = (await requests
                .OrderBy(r => r.SentAt).ThenBy(r => r.Id).Take(oldestCount)
                .Select(r => new { r.Id, Customer = r.Customer.Name, Product = r.Product.Name, r.RequestedCages, r.PickupDate, r.SentAt })
                .ToListAsync(cancellationToken))
            .Select(r => new PendingTaskRecord(r.Id, null, $"{r.Customer} – {r.RequestedCages} cages {r.Product}, pickup {Date(r.PickupDate)}", r.SentAt))
            .ToList();
        var oldestClaims = (await claims
                .OrderBy(c => c.SentAt).ThenBy(c => c.Id).Take(oldestCount)
                .Select(c => new { c.Id, c.Code, Customer = c.Customer.Name, Invoice = c.Invoice.Code, c.SentAt })
                .ToListAsync(cancellationToken))
            .Select(c => new PendingTaskRecord(c.Id, c.Code, $"{c.Customer} – invoice {c.Invoice}", c.SentAt))
            .ToList();
        // A payroll waits from the moment it was sent; the header written by the system or an older row has no sending time, so it waits from its creation.
        var oldestPayrolls = (await payrolls
                .OrderBy(p => p.SentAt ?? p.CreatedAt).ThenBy(p => p.Id).Take(oldestCount)
                .Select(p => new { p.Id, p.PeriodStart, p.PeriodEnd, Since = p.SentAt ?? p.CreatedAt })
                .ToListAsync(cancellationToken))
            .Select(p => new PendingTaskRecord(p.Id, null, $"Payroll {Date(p.PeriodStart)}–{Date(p.PeriodEnd)}", p.Since))
            .ToList();
        // An employee request waits from the moment the employee sent it; the label names the employee, the kind and the day or the days of leave.
        var oldestEmployeeRequests = (await employeeRequests
                .OrderBy(r => r.SentAt).ThenBy(r => r.Id).Take(oldestCount)
                .Select(r => new { r.Id, Employee = r.Employee.FullName, r.Type, r.FromDate, r.ToDate, r.SentAt })
                .ToListAsync(cancellationToken))
            .Select(r => new PendingTaskRecord(r.Id, null, $"{r.Employee} – {EmployeeRequestLabel(r.Type, r.FromDate, r.ToDate)}", r.SentAt))
            .ToList();
        return
        [
            new(PendingTaskKinds.PurchaseRequest, await requests.CountAsync(cancellationToken), oldestRequests),
            new(PendingTaskKinds.QualityClaim, await claims.CountAsync(cancellationToken), oldestClaims),
            new(PendingTaskKinds.Payroll, await payrolls.CountAsync(cancellationToken), oldestPayrolls),
            new(PendingTaskKinds.EmployeeRequest, await employeeRequests.CountAsync(cancellationToken), oldestEmployeeRequests),
        ];
    }

    private static string EmployeeRequestLabel(EmployeeRequestType type, DateOnly from, DateOnly to) => type switch
    {
        EmployeeRequestType.Leave when from == to => $"leave {Date(from)}",
        EmployeeRequestType.Leave => $"leave {Date(from)}–{Date(to)}",
        EmployeeRequestType.MissedCheckIn => $"missed check-in {Date(from)}",
        _ => $"missed check-out {Date(from)}",
    };


    private static string Date(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
}
