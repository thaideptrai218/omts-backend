using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface IPurchaseRequestRepository
{
    Task<PurchaseRequest?> GetAsync(Guid id, CancellationToken ct);
    Task<PurchaseRequest?> FindOperationAsync(Guid customerId, Guid operationId, CancellationToken ct);
    Task<(IReadOnlyList<PurchaseRequest> Items, int Total)> SearchAsync(Guid? customerId, int? statusId, int page, int pageSize, CancellationToken ct);
    void Add(PurchaseRequest request);
    Task<IReadOnlyList<Employee>> StaffingEmployeesAsync(CancellationToken ct);
    Task<IReadOnlyList<PurchaseShiftInvitation>> EmployeeInvitationsAsync(Guid employeeId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<PurchaseShiftInvitation?> InvitationAsync(Guid id, CancellationToken ct);
    Task<bool> HasShiftReservationAsync(Guid employeeId, DateOnly day, Guid? exceptRequestId, CancellationToken ct);
    void AddInvitation(PurchaseShiftInvitation invitation);
}
