using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Suppliers.Dtos;

public sealed record SupplierListItem(Guid Id, string Name, SupplierType SupplierType, string PhoneNumber, int StatusId, string Status);
