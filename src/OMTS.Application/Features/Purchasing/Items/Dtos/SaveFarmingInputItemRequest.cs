using OMTS.Domain.Enums;
namespace OMTS.Application.Features.Purchasing.Items.Dtos;
public sealed record SaveFarmingInputItemRequest(
    string? Code, string? Name, InputItemGroup? Group, string? Unit, string? Description, string? RowVersion = null);
