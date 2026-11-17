namespace OMTS.Application.Features.Workforce.Dtos;

public sealed record SaveWorkDayPlan(Guid RaftId, Guid JobTypeId, DateOnly WorkDate, Guid ResponsibleAccountId,
    Guid ClientOperationId, Guid? PurchaseRequestId = null, int? Cages = null, int? CagesPerEmployeePerDay = null,
    DateOnly? PickupDate = null, string? Note = null, string? RowVersion = null);
