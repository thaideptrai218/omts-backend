using System.Linq.Expressions;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

/// <summary>
/// The zone filter of the employees a zone-limited caller sees: those with a working zone among the caller's zones, and every seasonal worker, who has
/// no fixed zone and may be assigned work in any zone (decision of 25/11/2026). An empty zone list sees nobody.
/// </summary>
internal static class EmployeeZoneScope
{
    /// <summary>The employee's type in force on <paramref name="today"/> is Seasonal worker (the latest period from that day or before, else the first).</summary>
    public static Expression<Func<Employee, bool>> IsSeasonalOn(DateOnly today) =>
        e => (e.TypePeriods.Where(p => p.EffectiveFrom <= today).OrderByDescending(p => p.EffectiveFrom).Select(p => (EmployeeType?)p.Type).FirstOrDefault()
            ?? e.TypePeriods.OrderBy(p => p.EffectiveFrom).Select(p => (EmployeeType?)p.Type).FirstOrDefault()) == EmployeeType.SeasonalWorker;

    /// <summary>Working in <paramref name="zoneIds"/>, or a seasonal worker when <paramref name="includeSeasonal"/>; nobody for an empty zone list.</summary>
    public static Expression<Func<Employee, bool>> InZones(IReadOnlyCollection<Guid> zoneIds, bool includeSeasonal, DateOnly today)
    {
        if (zoneIds.Count == 0)
        {
            return _ => false;
        }

        Expression<Func<Employee, bool>> working = e => e.WorkingZones.Any(w => zoneIds.Contains(w.ZoneId));
        if (!includeSeasonal)
        {
            return working;
        }

        var seasonal = IsSeasonalOn(today);
        var body = new Rebind(seasonal.Parameters[0], working.Parameters[0]).Visit(seasonal.Body);
        return Expression.Lambda<Func<Employee, bool>>(Expression.OrElse(working.Body, body), working.Parameters[0]);
    }

    private sealed class Rebind(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
