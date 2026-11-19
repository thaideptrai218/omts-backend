using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Reports.Purchases;

/// <summary>
/// The purchases report (KiotViet "Nhà cung cấp – Hàng nhập theo NCC"; no VAT), worked out each time and never stored. The API checks the right (SUPPLIER_PAYMENTS View, or Export for the file: the
/// Cooperative Owner and the Accountant, decision D5 of 19/11/2026); purchases have no zone. The period is checked as every report does (MSG-GEN-01 names every field to fix); nothing found is MSG-GEN-10.
/// </summary>
public sealed class PurchaseReportService(ReportFilterResolver filters, IPurchaseReportQuery query, TimeProvider timeProvider)
{
    public const string GroupByField = "Grouping";
    public const string GroupKeyField = "Group key";
    public const string KindField = "Kind";
    public const string SupplierField = "Supplier";

    public Task<Result<PurchaseReport>> ReportAsync(PurchaseReportQuery request, CancellationToken cancellationToken) =>
        BuildAsync(request, PermissionAction.View, cancellationToken);

    /// <summary>The receipts and returns of one group of the same selection; MSG-GEN-10 when the group has none.</summary>
    public async Task<Result<PurchaseDocumentList>> DocumentsAsync(PurchaseReportQuery request, CancellationToken cancellationToken)
    {
        var selection = await SelectAsync(request, PermissionAction.View, string.IsNullOrWhiteSpace(request.GroupKey) ? [GroupKeyField] : [], cancellationToken);
        if (!selection.IsSuccess)
        {
            return selection.Error!;
        }

        var s = selection.Value!;
        var documents = PurchaseReportCalculator.DocumentsOf(s.Documents, s.Grouping, request.GroupKey!.Trim());
        var empty = documents.Count == 0 ? UserMessages.NoDataForFilters("group") : null;
        return new PurchaseDocumentList(s.From, s.To, s.Grouping.ToString().ToLowerInvariant(), request.GroupKey.Trim(), documents, empty?.Text, empty?.Code);
    }

    /// <summary>Shared with the export, which asks for the Export right.</summary>
    internal async Task<Result<PurchaseReport>> BuildAsync(PurchaseReportQuery request, PermissionAction action, CancellationToken cancellationToken)
    {
        var computedAt = timeProvider.GetUtcNow();
        var selection = await SelectAsync(request, action, [], cancellationToken);
        if (!selection.IsSuccess)
        {
            return selection.Error!;
        }

        var s = selection.Value!;
        var (groups, totals) = PurchaseReportCalculator.Build(s.Documents, s.Grouping);
        var empty = groups.Count == 0 ? UserMessages.NoDataForFilters("period and filters") : null;
        return new PurchaseReport(
            computedAt, s.From, s.To, s.Grouping.ToString().ToLowerInvariant(), new PurchaseReportChoices(s.Suppliers, s.KindName), groups, totals, empty?.Text, empty?.Code);
    }

    private sealed record Selection(
        DateOnly From, DateOnly To, PurchaseReportGrouping Grouping, IReadOnlyList<ReportChoice> Suppliers, string KindName, IReadOnlyList<PurchaseReportDocument> Documents);

    private async Task<Result<Selection>> SelectAsync(PurchaseReportQuery request, PermissionAction action, IReadOnlyList<string> otherInvalid, CancellationToken cancellationToken)
    {
        var invalid = otherInvalid.ToList();
        var grouping = ParseGrouping(request.GroupBy);
        if (grouping is null)
        {
            invalid.Add(GroupByField);
        }

        var (kind, kindOk) = ParseKind(request.Kind);
        if (!kindOk)
        {
            invalid.Add(KindField);
        }

        var supplierIds = (request.SupplierIds ?? []).Distinct().ToList();
        var names = supplierIds.Count > ReportFilterResolver.MaxChoices
            ? new Dictionary<Guid, string>()
            : await query.SupplierNamesAsync(supplierIds, cancellationToken);
        if (supplierIds.Count > ReportFilterResolver.MaxChoices || supplierIds.Exists(id => !names.ContainsKey(id)))
        {
            invalid.Add(SupplierField);
        }

        var filter = await filters.ResolveAsync(PermissionFunctions.SupplierPayments, action, request.From, request.To, null, null, null, invalid, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        var f = filter.Value!;
        var documents = await query.DocumentsAsync(f.From, f.To, supplierIds, kind, cancellationToken);
        return new Selection(
            f.From, f.To, grouping!.Value, supplierIds.Select(id => new ReportChoice(id, names[id])).ToList(), kind?.ToString() ?? "all", documents);
    }

    internal static PurchaseReportGrouping? ParseGrouping(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "supplier" => PurchaseReportGrouping.Supplier,
        "item" => PurchaseReportGrouping.Item,
        _ => null,
    };

    /// <summary>All when none or "all"; Seed or Supply in any letter case; anything else is not ok.</summary>
    internal static (PurchaseReceiptKind? Kind, bool Ok) ParseKind(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "all" => (null, true),
        "seed" => (PurchaseReceiptKind.Seed, true),
        "supply" => (PurchaseReceiptKind.Supply, true),
        _ => (null, false),
    };
}
