using OMTS.Domain.Entities.Identity;
using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class PurchaseRequestRepository(OmtsDbContext db) : IPurchaseRequestRepository
{
    public Task<PurchaseRequest?> GetAsync(Guid id, CancellationToken ct) =>
        db.Set<PurchaseRequest>().Include(r => r.Customer).Include(r => r.Product).Include(r => r.ShiftInvitations).ThenInclude(i => i.Employee).ThenInclude(e => e.Account)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
    public Task<PurchaseRequest?> FindOperationAsync(Guid customerId, Guid operationId, CancellationToken ct) =>
        db.Set<PurchaseRequest>().Include(r => r.Customer).Include(r => r.Product).Include(r => r.ShiftInvitations)
            .FirstOrDefaultAsync(r => r.CustomerId == customerId && r.ClientOperationId == operationId, ct);
    public async Task<(IReadOnlyList<PurchaseRequest> Items, int Total)> SearchAsync(Guid? customerId, int? statusId, int page, int pageSize, CancellationToken ct)
    {
        IQueryable<PurchaseRequest> q = db.Set<PurchaseRequest>().AsNoTracking().Include(r => r.Customer).Include(r => r.ShiftInvitations);
        if (customerId is { } c) q = q.Where(r => r.CustomerId == c);
        if (statusId is { } s) q = q.Where(r => r.StatusId == s);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }
    public void Add(PurchaseRequest request) => db.PurchaseRequests.Add(request);

    public async Task<IReadOnlyList<Employee>> StaffingEmployeesAsync(CancellationToken ct) =>
        await db.Employees.Include(e => e.Account).Where(e => e.StatusId == StatusIds.Employee.Working)
            .OrderBy(e => e.FullName).ToListAsync(ct);
    public async Task<IReadOnlyList<PurchaseShiftInvitation>> EmployeeInvitationsAsync(Guid employeeId, DateOnly from, DateOnly to, CancellationToken ct) =>
        await db.Set<PurchaseShiftInvitation>().AsNoTrackingWithIdentityResolution().Include(i => i.PurchaseRequest).ThenInclude(r => r.ShiftInvitations)
            .Where(i => i.EmployeeId == employeeId && i.PurchaseRequest.WorkDate >= from && i.PurchaseRequest.WorkDate <= to)
            .OrderBy(i => i.PurchaseRequest.WorkDate).ThenBy(i => i.CreatedAt).ToListAsync(ct);
    public Task<PurchaseShiftInvitation?> InvitationAsync(Guid id, CancellationToken ct) =>
        db.Set<PurchaseShiftInvitation>().Include(i => i.Employee).ThenInclude(e => e.Account)
            .Include(i => i.PurchaseRequest).ThenInclude(r => r.ShiftInvitations)
            .FirstOrDefaultAsync(i => i.Id == id, ct);
    public Task<bool> HasShiftReservationAsync(Guid employeeId, DateOnly day, Guid? exceptRequestId, CancellationToken ct) =>
        db.Set<PurchaseShiftInvitation>().AnyAsync(i => i.EmployeeId == employeeId && i.ReservedWorkDate == day &&
            (exceptRequestId == null || i.PurchaseRequestId != exceptRequestId), ct);
    public void AddInvitation(PurchaseShiftInvitation invitation) => db.Set<PurchaseShiftInvitation>().Add(invitation);

    public Task<PurchaseRequest?> GetForEditAsync(Guid requestId, CancellationToken cancellationToken) =>
        db.PurchaseRequests.Include(r => r.Customer).Include(r => r.Product).FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken);

    public async Task<IReadOnlyList<PurchaseRequest>> ListAcceptedOfCustomerAsync(Guid customerId, DateOnly earliestPickup, CancellationToken cancellationToken) =>
        await db.PurchaseRequests.AsNoTracking().Include(r => r.Product)
            .Where(r => r.CustomerId == customerId && r.StatusId == StatusIds.PurchaseRequest.Accepted && r.AgreedPickupDate >= earliestPickup)
            .OrderBy(r => r.AgreedPickupDate).ThenBy(r => r.SentAt)
            .ToListAsync(cancellationToken);
}
