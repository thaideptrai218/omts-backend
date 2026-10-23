using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Domain.Constants;

namespace OMTS.Application.Features.Workforce;

/// <summary>In Use ↔ Discontinued of workers and teams (LAB-02, USAGE transitions 1 and 2).</summary>
public static class UsageStatusChange
{
    /// <summary>MSG61 when the record is already in the target status, else null.</summary>
    public static AppError? SameStatus(IStatusCatalog statusCatalog, string entity, int currentStatusId, int targetStatusId)
    {
        if (currentStatusId != targetStatusId)
        {
            return null;
        }

        var name = statusCatalog.DisplayNameOf(targetStatusId);
        return AppError.Conflict(UserMessages.CannotChangeStatus(entity, name, name, "it is already in this status"));
    }

    public static int Opposite(int statusId) =>
        statusId == StatusIds.Usage.InUse ? StatusIds.Usage.Discontinued : StatusIds.Usage.InUse;
}
