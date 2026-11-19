using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Application.Features.Reports.Financial;

/// <summary>
/// The financial report (KiotViet "Tài chính", the business result of a period), worked out each time and never stored. The API checks the right (SALES_REPORTS View, or Export for the file: the
/// Cooperative Owner and the Accountant, decision D5 of 19/11/2026). The figures are cooperative-wide, so a user limited to some zones is refused (403 MSG-GEN-08). The period is at most five years
/// (MSG-GEN-01); when none is given it runs from 1 January to today. All zero is MSG-GEN-10.
/// </summary>
public sealed class FinancialReportService(ReportFilterResolver filters, IFinancialReportQuery query, TimeProvider timeProvider)
{
    public const string UnitField = "Unit";

    public Task<Result<FinancialReport>> ReportAsync(FinancialReportQuery request, CancellationToken cancellationToken) =>
        BuildAsync(request, PermissionAction.View, cancellationToken);

    /// <summary>Shared with the export, which asks for the Export right.</summary>
    internal async Task<Result<FinancialReport>> BuildAsync(FinancialReportQuery request, PermissionAction action, CancellationToken cancellationToken)
    {
        var computedAt = timeProvider.GetUtcNow();
        var today = BusinessClock.ToBusinessDate(computedAt);
        var unit = FinancialReportCalculator.ParseUnit(request.Unit);
        // The year so far when no date is given (the other reports default to the month).
        var (from, to) = (request.From, request.To) switch
        {
            (null, null) => (new DateOnly(today.Year, 1, 1), today),
            (null, { } end) => (new DateOnly(end.Year, 1, 1), end),
            var (start, end) => (start, end),
        };
        var filter = await filters.ResolveAsync(
            PermissionFunctions.SalesReports, action, from, to, null, null, null, unit is null ? [UnitField] : [], cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        var f = filter.Value!;
        if (f.ZoneIds is not null)
        {
            return AppError.Forbidden;
        }

        var columns = FinancialReportCalculator.Columns(f.From, f.To, unit!.Value);
        var entries = await query.EntriesAsync(f.From, f.To, cancellationToken);
        var rows = FinancialReportCalculator.Rows(entries, columns);
        var empty = rows.All(r => r.Total == 0m) ? UserMessages.NoDataForFilters("period") : null;
        return new FinancialReport(computedAt, f.From, f.To, unit.Value.ToString().ToLowerInvariant(), columns, rows, empty?.Text, empty?.Code);
    }
}
