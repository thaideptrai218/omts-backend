namespace OMTS.Application.Features.Sales.Requests.Dtos;

public sealed record PurchaseShiftInvitationView(Guid Id, Guid EmployeeId, string EmployeeName, bool? Approved,
    string? Reason, DateTimeOffset? RespondedAt);
