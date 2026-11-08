using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Common;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Repositories;

/// <summary>Re-points the rows of <see cref="References"/>, the one list of what refers to a customer profile.</summary>
internal sealed class CustomerReferences(OmtsDbContext db) : ICustomerReferences
{
    /// <summary>
    /// Every column that names a customer profile. A new table adds its line here; a test compares this list with the
    /// foreign keys of the model, so a table left out fails the build. The writers of such a table must
    /// <c>MarkChanged</c> the customer row, so that a save and a merge at the same moment conflict (409 MSG06) and no new
    /// row can stay on the merged profile.
    /// </summary>
    private static readonly IReadOnlyList<IReference> References =
    [
        // A profile merged earlier into the one being merged now: it follows, so a merged profile always points at a kept one.
        new Reference<Customer>("Merged customer profiles", c => c.MergedIntoCustomerId),
        new Reference<PurchaseRequest>("Purchase requests", r => r.CustomerId),
        new Reference<SalesInvoice>("Sales invoices", i => i.CustomerId),
        new Reference<CustomerPayment>("Customer payments", p => p.CustomerId),
        // The invoice the transfer saves then names the kept profile.
        new Reference<PendingSale>("Sales awaiting their transfer", s => s.CustomerId),
    ];

    public IReadOnlyList<CustomerReferenceColumn> Columns { get; } = References.Select(r => r.Describe()).ToList();

    public async Task<IReadOnlyList<CustomerReferenceCount>> CountAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var counts = new List<CustomerReferenceCount>();
        foreach (var reference in References)
        {
            counts.Add(new CustomerReferenceCount(reference.Name, await reference.CountAsync(db, customerId, cancellationToken)));
        }

        return counts;
    }

    public async Task<IReadOnlyList<MovedCustomerReference>> RepointAsync(Guid fromCustomerId, Guid toCustomerId, CancellationToken cancellationToken)
    {
        var moved = new List<MovedCustomerReference>();
        foreach (var reference in References)
        {
            var rowIds = await reference.RepointAsync(db, fromCustomerId, toCustomerId, cancellationToken);
            if (rowIds.Count > 0)
            {
                moved.Add(new MovedCustomerReference(reference.Name, rowIds));
            }
        }

        return moved;
    }

    private interface IReference
    {
        string Name { get; }

        CustomerReferenceColumn Describe();

        Task<int> CountAsync(OmtsDbContext db, Guid customerId, CancellationToken cancellationToken);

        Task<IReadOnlyList<Guid>> RepointAsync(OmtsDbContext db, Guid fromCustomerId, Guid toCustomerId, CancellationToken cancellationToken);
    }

    /// <param name="column">The customer id of a row; a required column converts to <c>Guid?</c> on its own.</param>
    private sealed class Reference<TRow>(string name, Expression<Func<TRow, Guid?>> column) : IReference
        where TRow : Entity
    {
        private readonly string _columnName = ((MemberExpression)(column.Body is UnaryExpression converted ? converted.Operand : column.Body)).Member.Name;

        public string Name { get; } = name;

        public CustomerReferenceColumn Describe() => new(Name, typeof(TRow), _columnName);

        public Task<int> CountAsync(OmtsDbContext db, Guid customerId, CancellationToken cancellationToken) =>
            db.Set<TRow>().AsNoTracking().CountAsync(Naming(customerId), cancellationToken);

        public async Task<IReadOnlyList<Guid>> RepointAsync(OmtsDbContext db, Guid fromCustomerId, Guid toCustomerId, CancellationToken cancellationToken)
        {
            // Rows are loaded, not updated in bulk: their row versions and the audit stamps of the save still apply.
            var rows = await db.Set<TRow>().Where(Naming(fromCustomerId)).ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                db.Entry(row).Property(_columnName).CurrentValue = toCustomerId;
            }

            return rows.Select(r => r.Id).ToList();
        }

        private Expression<Func<TRow, bool>> Naming(Guid customerId) => Expression.Lambda<Func<TRow, bool>>(
            Expression.Equal(column.Body, Expression.Constant((Guid?)customerId, typeof(Guid?))), column.Parameters);
    }
}
