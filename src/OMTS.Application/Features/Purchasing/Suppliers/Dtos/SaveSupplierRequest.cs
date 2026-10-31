using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Suppliers.Dtos;

public sealed record SaveSupplierRequest(string? Name, SupplierType? SupplierType, string? PhoneNumber, string? OtherContacts, string? RowVersion = null);
