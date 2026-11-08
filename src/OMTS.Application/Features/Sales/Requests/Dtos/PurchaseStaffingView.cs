namespace OMTS.Application.Features.Sales.Requests.Dtos;

public sealed record PurchaseStaffingView(int Cages, DateOnly WorkDate, DateOnly PickupDate, int CagesPerEmployeePerDay,
    int RequiredEmployees, int InvitedEmployees, int ApprovedEmployees, int DeclinedEmployees, int PendingEmployees,
    bool HasEnoughStaff, string? Note);
