using OMTS.Domain.Enums;
namespace OMTS.Application.Features.Purchasing.Items.Dtos;
public sealed record FarmingInputItemView(
    Guid Id, string Code, string Name, InputItemGroup Group, string Unit, string? Description, bool IsUsed, int StatusId, string Status, string RowVersion);
