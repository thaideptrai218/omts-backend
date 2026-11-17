namespace OMTS.Application.Features.Sales.Requests.Dtos;

public sealed record MyPurchaseShiftView(Guid Id, Guid PurchaseRequestId, string RequestCode, string ProductName,
    int Cages, DateOnly WorkDate, DateOnly PickupDate, int RequiredEmployees, int ApprovedEmployees, bool? Approved,
    string? Reason, string? Note, string RequestStatus, bool CanRespond, string RowVersion);
