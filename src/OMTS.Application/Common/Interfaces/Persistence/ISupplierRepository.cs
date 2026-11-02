using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface ISupplierRepository
{
    Task<Supplier?> GetForEditAsync(Guid supplierId, CancellationToken cancellationToken);

    /// <summary>Another supplier with this phone number.</summary>
    Task<Supplier?> FindByPhoneAsync(string phoneNumber, Guid? exceptSupplierId, CancellationToken cancellationToken);

    /// <summary>By name or phone (contains), optional type and status, ordered by name.</summary>
    Task<(IReadOnlyList<Supplier> Items, int Total)> SearchAsync(
        string? search, SupplierType? type, int? statusId, int page, int pageSize, CancellationToken cancellationToken);

    void Add(Supplier supplier);
}
