using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.AccessManagement.Dtos;

/// <param name="ConfirmMessage">MSG09, shown before saving an edit of a role in use.</param>
public sealed record RoleImpact(int AccountCount, int ZoneCount, bool AllZonesGranted, IReadOnlyList<string> Accounts, string ConfirmMessage);
