using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class LaborPaymentRepository(OmtsDbContext db) : ILaborPaymentRepository
{
    public Task<LaborPayment?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.LaborPayments
            .Include(p => p.Settlement).ThenInclude(s => s!.Payroll)
            .Include(p => p.Settlement).ThenInclude(s => s!.Payments)
            .Include(p => p.Employee)
            .Include(p => p.Worker)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task<IReadOnlyList<LaborPayment>> ListAdvancesOfAsync(
        IReadOnlyCollection<Guid> employeeIds, IReadOnlyCollection<Guid> workerIds, CancellationToken cancellationToken) =>
        employeeIds.Count == 0 && workerIds.Count == 0
            ? []
            : await Advances(db.LaborPayments)
                .Where(p => (p.EmployeeId != null && employeeIds.Contains(p.EmployeeId.Value))
                    || (p.WorkerId != null && workerIds.Contains(p.WorkerId.Value)))
                .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<LaborPayment>> ListAllAdvancesAsync(CancellationToken cancellationToken) =>
        await Advances(db.LaborPayments.AsNoTracking()).ToListAsync(cancellationToken);

    public async Task<(IReadOnlyList<LaborPayment> Items, int Total)> PageAdvancesAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var total = await db.LaborPayments.AsNoTracking().CountAsync(p => p.IsAdvance && p.OriginalPaymentId == null, cancellationToken);
        var items = await Advances(db.LaborPayments.AsNoTracking()).Where(p => p.OriginalPaymentId == null)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<DeductedAdvanceTotals> GetDeductedAdvanceTotalsAsync(
        IReadOnlyCollection<Guid> employeeIds, IReadOnlyCollection<Guid> workerIds, CancellationToken cancellationToken)
    {
        if (employeeIds.Count == 0 && workerIds.Count == 0)
        {
            return DeductedAdvanceTotals.None;
        }

        var lines = await db.PayLines.AsNoTracking()
            .Where(l => l.LineType == PayLineType.AdvanceDeduction && l.Settlement != null
                && l.Settlement.StatusId != StatusIds.LaborSettlement.Draft
                && ((l.Settlement.EmployeeId != null && employeeIds.Contains(l.Settlement.EmployeeId.Value))
                    || (l.Settlement.WorkerId != null && workerIds.Contains(l.Settlement.WorkerId.Value))))
            .Select(l => new { l.Settlement!.EmployeeId, l.Settlement.WorkerId, l.Amount })
            .ToListAsync(cancellationToken);

        return new DeductedAdvanceTotals(
            lines.Where(l => l.EmployeeId != null).GroupBy(l => l.EmployeeId!.Value).ToDictionary(g => g.Key, g => g.Sum(l => l.Amount)),
            lines.Where(l => l.WorkerId != null).GroupBy(l => l.WorkerId!.Value).ToDictionary(g => g.Key, g => g.Sum(l => l.Amount)));
    }

    public void Add(LaborPayment payment) => db.LaborPayments.Add(payment);

    private static IQueryable<LaborPayment> Advances(IQueryable<LaborPayment> query) =>
        query.Where(p => p.IsAdvance)
            .Include(p => p.Employee)
            .Include(p => p.Worker)
            .Include(p => p.DeductedInSettlement).ThenInclude(s => s!.Payroll)
            .OrderBy(p => p.PaymentDate).ThenBy(p => p.CreatedAt);
}
