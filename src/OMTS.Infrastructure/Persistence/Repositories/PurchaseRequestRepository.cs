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
    public void Add(PurchaseRequest request) => db.PurchaseRequests.Add(request);

    public Task<PurchaseRequest?> GetAsync(Guid requestId, CancellationToken cancellationToken) =>
        db.PurchaseRequests.Include(r => r.Customer).Include(r => r.Product)
            .Include(r => r.ShiftInvitations).ThenInclude(i => i.Employee).ThenInclude(e => e.Account)
            .AsSplitQuery()
            .FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken);

    public Task<PurchaseRequest?> FindOperationAsync(Guid customerId, Guid operationId, CancellationToken cancellationToken) =>
        db.PurchaseRequests.Include(r => r.Customer).Include(r => r.Product)
            .FirstOrDefaultAsync(r => r.CustomerId == customerId && r.ClientOperationId == operationId, cancellationToken);

    public async Task<(IReadOnlyList<PurchaseRequest> Items, int Total)> ListOfCustomerAsync(Guid customerId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.PurchaseRequests.AsNoTracking().Where(r => r.CustomerId == customerId);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.Include(r => r.Product)
            .OrderByDescending(r => r.SentAt).ThenBy(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<(IReadOnlyList<PurchaseRequest> Items, int Total)> SearchAsync(int? statusId, Guid? customerId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.PurchaseRequests.AsNoTracking();
        if (statusId is not null)
        {
            query = query.Where(r => r.StatusId == statusId);
        }

        if (customerId is not null)
        {
            query = query.Where(r => r.CustomerId == customerId);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.Include(r => r.Customer).Include(r => r.Product).Include(r => r.ShiftInvitations)
            .OrderBy(r => r.StatusId == StatusIds.PurchaseRequest.AwaitingResponse || r.StatusId == StatusIds.PurchaseRequest.AwaitingStaff ? 0 : 1)
            .ThenByDescending(r => r.SentAt).ThenBy(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<IReadOnlyList<PurchaseRequestSending>> ListSendingsAsync(IReadOnlyCollection<Guid> requestIds, CancellationToken cancellationToken) =>
        await db.Notifications.AsNoTracking()
            .Where(n => n.RelatedRecordType == RelatedRecordTypes.PurchaseRequest && n.RelatedRecordId != null && requestIds.Contains(n.RelatedRecordId.Value)
                && n.Channel != NotificationChannel.InApp)
            .OrderBy(n => n.CreatedAt)
            .Select(n => new PurchaseRequestSending(n.RelatedRecordId!.Value, n.Channel, n.PhoneNumber, n.DeliveryStatusId, n.DeliveryStatusAt))
            .ToListAsync(cancellationToken);

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

    public async Task<IReadOnlyList<PurchaseRequest>> ListOpenForHarvestAsync(CancellationToken cancellationToken) =>
        await db.PurchaseRequests.AsNoTracking().Include(r => r.Customer).Include(r => r.WorkDayPlan)
            .Where(r => r.StatusId == StatusIds.PurchaseRequest.Accepted && r.HarvestCompletedAt == null)
            .OrderBy(r => r.AgreedPickupDate).ThenBy(r => r.SentAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PurchaseRequest>> ListAcceptedOfCustomerAsync(Guid customerId, DateOnly earliestPickup, CancellationToken cancellationToken) =>
        await db.PurchaseRequests.AsNoTracking().Include(r => r.Product)
            .Where(r => r.CustomerId == customerId && r.StatusId == StatusIds.PurchaseRequest.Accepted && r.AgreedPickupDate >= earliestPickup)
            .OrderBy(r => r.AgreedPickupDate).ThenBy(r => r.SentAt)
            .ToListAsync(cancellationToken);
}
