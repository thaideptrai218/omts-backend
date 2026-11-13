using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface ICustomerDebtAdjustmentRepository
{
    void Add(CustomerDebtAdjustment adjustment);

    /// <summary>Tracked, with its customer.</summary>
    Task<CustomerDebtAdjustment?> GetForEditAsync(Guid adjustmentId, CancellationToken cancellationToken);

    /// <summary>Read only, with its customer.</summary>
    Task<CustomerDebtAdjustment?> GetAsync(Guid adjustmentId, CancellationToken cancellationToken);
}
