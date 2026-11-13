using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.Labor.Work;

/// <summary>The view of an employee request every screen uses (the employee must be loaded with the request).</summary>
internal static class EmployeeRequestViews
{
    public static EmployeeRequestView Of(EmployeeRequest request, IStatusCatalog statusCatalog) => new(
        request.Id,
        request.EmployeeId,
        request.Employee.FullName,
        request.Type,
        request.FromDate,
        request.ToDate,
        request.RequestedTime,
        request.Reason,
        request.StatusId switch
        {
            StatusIds.EmployeeRequest.Approved => true,
            StatusIds.EmployeeRequest.Rejected => false,
            _ => null,
        },
        request.ReviewNote,
        RowVersionGuard.Encode(request.RowVersion),
        request.CancelledAt,
        request.StatusId,
        statusCatalog.DisplayNameOf(request.StatusId),
        request.SentAt,
        request.ReviewedAt);
}
