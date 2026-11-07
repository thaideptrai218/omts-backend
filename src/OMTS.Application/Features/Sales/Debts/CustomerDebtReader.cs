using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Application.Features.Sales.Debts;

/// <summary>The customer debt of one or more profiles now (BR-DEBT-02): the database sums through <see cref="CustomerDebtMath"/>, so every screen shows the same figure.</summary>
public sealed class CustomerDebtReader(ICustomerDebtQuery query)
{
    public async Task<decimal> DebtAsync(Guid customerId, CancellationToken cancellationToken) =>
        (await DebtsAsync([customerId], cancellationToken))[customerId];

    /// <summary>Every id asked for has an entry.</summary>
    public async Task<IReadOnlyDictionary<Guid, decimal>> DebtsAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        var ids = customerIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, decimal>();
        }

        var figures = await query.FiguresAsync(ids, cancellationToken);
        return ids.ToDictionary(id => id, id => CustomerDebtMath.Debt(figures.GetValueOrDefault(id, CustomerDebtFigures.None)));
    }
}
