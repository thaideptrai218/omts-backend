namespace OMTS.Application.Features.Workforce.Dtos;

public sealed record WorkDayPlanView(Guid Id, DateOnly WorkDate, Guid RaftId, string RaftCode, Guid ZoneId, string ZoneName,
    Guid JobTypeId, string JobName, Guid ResponsibleAccountId, string ResponsibleName, string CreatorName,
    string Status, string? ResponseReason, bool CanRespond, bool CanAssign, Guid? PurchaseRequestId, string? RequestCode,
    int? Cages, int? RequiredEmployees, int InvitedEmployees, int ApprovedEmployees, int AssignedEmployees,
    string? Note, string RowVersion);
