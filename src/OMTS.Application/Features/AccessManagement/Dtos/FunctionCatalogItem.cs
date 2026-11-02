using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.AccessManagement.Dtos;

public sealed record FunctionCatalogItem(string Code, string Name, bool CooperativeWide, bool IsDashboardTile, IReadOnlyList<string> SupportedActions);
