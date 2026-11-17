namespace OMTS.Application.Features.Sales.Requests.Dtos;

public sealed record PlanPurchaseStaffing(int Cages, DateOnly WorkDate, DateOnly PickupDate, int CagesPerEmployeePerDay,
    IReadOnlyList<Guid>? EmployeeIds, string? Note, string? RowVersion);
