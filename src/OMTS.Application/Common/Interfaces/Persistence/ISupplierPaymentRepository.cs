using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface ISupplierPaymentRepository
{
    Task<SupplierPayment?> GetForEditAsync(Guid paymentId, CancellationToken cancellationToken);

    Task<SupplierPayment?> FindByDeduplicationKeyAsync(string key, CancellationToken cancellationToken);

    /// <summary>Payments by supplier, receipt, zone (of the receipt), method, status and date range, newest first.</summary>
    Task<(IReadOnlyList<SupplierPayment> Items, int Total, decimal TotalAmount)> SearchAsync(
        Guid? supplierId, Guid? receiptId, Guid? zoneId, PaymentMethod? method, int? statusId, DateOnly? from, DateOnly? to,
        int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Recorded payments not allocated to any receipt, per supplier.</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> UnallocatedAsync(IReadOnlyCollection<Guid>? supplierIds, CancellationToken cancellationToken);

    void Add(SupplierPayment payment);
}
