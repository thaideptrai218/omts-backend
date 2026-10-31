using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Suppliers.Dtos;

public sealed record SupplierReceiptRow(Guid Id, string Code, DateOnly? ReceiptDate, Guid ZoneId, string Status, decimal Payable, decimal Outstanding);
