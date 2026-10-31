using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Employees.Dtos;

/// <summary>Asks the web to let the Owner review the linked account (lock it, or unlock / re-grant it).</summary>
public sealed record FollowUp(string Action, Guid AccountId);
