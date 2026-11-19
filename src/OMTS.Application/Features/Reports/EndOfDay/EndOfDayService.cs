using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.CashBook;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Reports.EndOfDay;

/// <summary>The report with the period it was worked out for, which the file needs to word its heading.</summary>
internal sealed record EndOfDayResult(EndOfDayReport Report, EndOfDayPeriod Period);

/// <summary>
/// The end-of-day report (KiotViet "Cuối ngày") of one concern (sales, money, goods or a summary of the three) over a period given in hours, worked out each time and never stored. The API checks
/// the right (SALES_REPORTS View, or Export for the file: the Cooperative Owner and the Accountant, decision D5 of 19/11/2026). The zones limit the sales, the goods and the lots; the money is
/// the cash book of the period (it has no zone), shown only to a user of every zone. The money reuses the cash book query, so its sums are those of the cash book. MSG-GEN-01 names every field
/// to fix at once (the period is named "to").
/// </summary>
public sealed class EndOfDayService(
    ReportFilterResolver filters,
    IEndOfDayReportQuery query,
    ICashBookQuery cashBook,
    CashBookLookupService cashLookups,
    ICashVoucherTypeRepository voucherTypes,
    IAccountRepository accounts,
    ZoneAccessGuard zoneAccess,
    TimeProvider timeProvider)
{
    public const string ConcernField = "concern";
    public const string PaymentMethodsField = "paymentMethods";
    public const string ChannelsField = "channels";
    public const string PaymentTermsField = "paymentTerms";
    public const string FundsField = "funds";
    public const string SourcesField = "sources";
    public const string PartyKindsField = "partyKinds";
    public const string TypeIdsField = "typeIds";
    public const string CategoryIdsField = "categoryIds";
    public const string ReceivedByField = "receivedBy";
    public const string CreatedByField = "createdBy";

    public async Task<Result<EndOfDayReport>> ReportAsync(EndOfDayQuery request, CancellationToken cancellationToken)
    {
        var built = await BuildAsync(request, PermissionAction.View, cancellationToken);
        return built.IsSuccess ? built.Value!.Report : built.Error!;
    }

    /// <summary>Shared with the export, which asks for the Export right.</summary>
    internal async Task<Result<EndOfDayResult>> BuildAsync(EndOfDayQuery request, PermissionAction action, CancellationToken cancellationToken)
    {
        var computedAt = timeProvider.GetUtcNow();
        var today = BusinessClock.ToBusinessDate(computedAt);
        var invalid = new List<string>();
        var concern = ParseConcern(request.Concern, invalid);
        var period = EndOfDayPeriod.Resolve(request.Date, request.From, request.To, today);
        if (!period.IsSuccess && period.Error!.Details?.GetValueOrDefault("field") is string periodField)
        {
            invalid.Add(periodField);
        }

        var sales = concern == EndOfDayConcern.Sales;
        var money = concern == EndOfDayConcern.Money;
        var goods = concern == EndOfDayConcern.Goods;
        var customerIds = sales || goods ? Distinct(request.CustomerIds) : [];
        var productIds = goods ? Distinct(request.ProductIds) : [];
        var categoryIds = goods ? Distinct(request.CategoryIds) : [];
        var receivedBy = sales ? Distinct(request.ReceivedBy) : [];
        var createdBy = Distinct(request.CreatedBy);
        var typeIds = money ? Distinct(request.TypeIds) : [];
        var paymentMethods = sales ? ParseEnums<CustomerPaymentMethod>(request.PaymentMethods, PaymentMethodsField, invalid) : [];
        var channels = sales ? ParseEnums<SalesOrderSource>(request.Channels, ChannelsField, invalid) : [];
        var paymentTerms = sales ? ParseEnums<SalesPaymentTerm>(request.PaymentTerms, PaymentTermsField, invalid) : [];
        var funds = money ? ParseEnums<CashFund>(request.Funds, FundsField, invalid) : [];
        var sources = money ? ParseEnums<CashSource>(request.Sources, SourcesField, invalid) : [];
        var partyKinds = money ? ParseEnums<CashPartyKind>(request.PartyKinds, PartyKindsField, invalid) : [];
        AddTooMany(invalid, receivedBy.Count, ReceivedByField);
        AddTooMany(invalid, createdBy.Count, CreatedByField);
        AddTooMany(invalid, typeIds.Count, TypeIdsField);
        AddTooMany(invalid, categoryIds.Count, CategoryIdsField);

        // The period is read as dates by the resolver: a wrong one is already named in `invalid`, so any day will do for it.
        var fromDate = period.IsSuccess ? period.Value!.FromDate : today;
        var toDate = period.IsSuccess ? period.Value!.ToDate : today;
        var filter = await filters.ResolveAsync(PermissionFunctions.SalesReports, action, fromDate, toDate, request.ZoneIds, customerIds, productIds, invalid, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        var f = filter.Value!;
        var span = period.Value!;
        var scope = new EndOfDayScope(span.FromInstant, span.ToExclusive, f.ZoneIds);

        var people = await NamesOfAsync(receivedBy, createdBy, cancellationToken);
        if (people.Invalid is { } unknownPerson)
        {
            return AppError.Invalid(unknownPerson);
        }

        var types = typeIds.Count == 0 ? new Dictionary<Guid, string>() : (await voucherTypes.GetByIdsAsync(typeIds, cancellationToken)).ToDictionary(t => t.Key, t => t.Value.Name);
        if (typeIds.Exists(id => !types.ContainsKey(id)))
        {
            return AppError.Invalid(TypeIdsField);
        }

        var categories = new List<EndOfDayCategory>();
        IReadOnlyCollection<Guid> categoryScope = [];
        if (categoryIds.Count > 0)
        {
            var all = await query.CategoriesAsync(cancellationToken);
            if (categoryIds.Exists(id => all.All(c => c.Id != id)))
            {
                return AppError.Invalid(CategoryIdsField);
            }

            categories = categoryIds.Select(id => all.First(c => c.Id == id)).ToList();
            categoryScope = EndOfDayCalculator.WithDescendants(categoryIds, all);
        }

        var choices = new EndOfDayChoices(
            f.Zones, f.Customers, f.Products, categories.Select(c => new ReportChoice(c.Id, c.Name)).ToList(), people.All, people.Named(receivedBy), people.Named(createdBy),
            typeIds.Select(id => new ReportChoice(id, types[id])).ToList(), Names(paymentMethods), Names(channels), Names(paymentTerms), Names(funds), Names(sources), Names(partyKinds));

        EndOfDaySales? salesBlock = null;
        EndOfDayMoney? moneyBlock = null;
        EndOfDayGoods? goodsBlock = null;
        EndOfDaySummary? summary = null;
        bool empty;
        switch (concern)
        {
            case EndOfDayConcern.Sales:
                salesBlock = EndOfDayCalculator.SalesOf(await query.SalesAsync(scope, new EndOfDaySalesCriteria(customerIds, receivedBy, createdBy, paymentMethods, channels, paymentTerms), cancellationToken));
                empty = IsEmpty(salesBlock);
                break;
            case EndOfDayConcern.Money:
                moneyBlock = await MoneyAsync(span, funds, sources, typeIds, partyKinds, createdBy, action, cancellationToken);
                empty = moneyBlock.Entries.Count == 0;
                break;
            case EndOfDayConcern.Goods:
                goodsBlock = EndOfDayCalculator.GoodsOf(
                    await query.GoodsAsync(scope, new EndOfDayGoodsCriteria(customerIds, createdBy), cancellationToken), await query.LotsAsync(scope, cancellationToken), productIds, categoryScope);
                empty = goodsBlock.Products.Count == 0 && goodsBlock.Lots.Count == 0;
                break;
            default:
                var summarySales = EndOfDayCalculator.SalesOf(await query.SalesAsync(scope, new EndOfDaySalesCriteria([], [], createdBy, [], [], []), cancellationToken));
                var summaryGoods = EndOfDayCalculator.GoodsOf(
                    await query.GoodsAsync(scope, new EndOfDayGoodsCriteria([], createdBy), cancellationToken), await query.LotsAsync(scope, cancellationToken), [], []);
                var summaryMoney = await MoneyAsync(span, [], [], [], [], createdBy, action, cancellationToken);
                summary = new EndOfDaySummary(
                    EndOfDayCalculator.SummaryOf(summarySales), new EndOfDayMoneySummary(summaryMoney.ByFund, summaryMoney.Totals), EndOfDayCalculator.SummaryOf(summaryGoods));
                empty = IsEmpty(summarySales) && summaryMoney.Entries.Count == 0 && summaryGoods.Products.Count == 0 && summaryGoods.Lots.Count == 0;
                break;
        }

        var message = empty ? UserMessages.NoDataForFilters("period and filters") : null;
        var report = new EndOfDayReport(computedAt, ConcernName(concern), span.FromText, span.ToText, choices, salesBlock, moneyBlock, goodsBlock, summary, message?.Text, message?.Code);
        return new EndOfDayResult(report, span);
    }

    /// <summary>
    /// The recorded lines of the cash book in the period, trimmed to the hour (the cash book filters by day), then limited by the funds, sources, types, party kinds and creators chosen. One
    /// choice goes into the cash book query itself; several are applied here. Nothing for a user who does not read every zone.
    /// </summary>
    private async Task<EndOfDayMoney> MoneyAsync(
        EndOfDayPeriod period, List<CashFund> funds, List<CashSource> sources, List<Guid> typeIds, List<CashPartyKind> partyKinds, List<Guid> createdBy, PermissionAction action,
        CancellationToken cancellationToken)
    {
        if (!(await zoneAccess.PermittedZonesAsync(PermissionFunctions.SalesReports, action, cancellationToken)).AllZones)
        {
            return EndOfDayCalculator.MoneyOf([]);
        }

        var fund = funds.Count == 1 ? Enum.Parse<CashFundFilter>(funds[0].ToString()) : CashFundFilter.All;
        var lines = await cashBook.ListAsync(
            new CashBookQuery(
                fund, null, period.FromDate, period.ToDate, null, typeIds.Count == 1 ? typeIds[0] : null, CashEntryStatus.Recorded, createdBy.Count == 1 ? createdBy[0] : null,
                partyKinds.Count == 1 ? partyKinds[0] : null, null, null, null, CashBookSort.Time, false),
            cancellationToken);
        var from = period.FromInstant;
        var to = period.ToExclusive;
        var kept = lines
            .Where(e => e.OccurredAt >= from && e.OccurredAt < to)
            .Where(e => funds.Count == 0 || funds.Exists(x => x.ToString() == e.Fund))
            .Where(e => sources.Count == 0 || sources.Exists(x => x.ToString() == e.Source))
            .Where(e => typeIds.Count == 0 || typeIds.Contains(e.TypeId))
            .Where(e => partyKinds.Count == 0 || (e.PartyKind is not null && partyKinds.Exists(x => x.ToString() == e.PartyKind)))
            .Where(e => createdBy.Count == 0 || (e.CreatedById is { } by && createdBy.Contains(by)))
            .ToList();
        var lookups = await cashLookups.LoadAsync(kept, cancellationToken);
        return EndOfDayCalculator.MoneyOf(kept.Select(e => EndOfDayCalculator.EntryOf(lookups.Row(e))).ToList());
    }

    private async Task<PeopleNames> NamesOfAsync(List<Guid> receivedBy, List<Guid> createdBy, CancellationToken cancellationToken)
    {
        var ids = receivedBy.Concat(createdBy).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new PeopleNames(new Dictionary<Guid, string>(), null);
        }

        var names = await accounts.GetDisplayNamesAsync(ids, cancellationToken);
        var unknown = receivedBy.Exists(id => !names.ContainsKey(id)) ? ReceivedByField : createdBy.Exists(id => !names.ContainsKey(id)) ? CreatedByField : null;
        return new PeopleNames(names, unknown);
    }

    private sealed record PeopleNames(IReadOnlyDictionary<Guid, string> Names, string? Invalid)
    {
        public IReadOnlyList<ReportChoice> Named(IEnumerable<Guid> ids) => ids.Where(Names.ContainsKey).Select(id => new ReportChoice(id, Names[id])).ToList();

        public IReadOnlyList<ReportChoice> All => Names.Select(n => new ReportChoice(n.Key, n.Value)).OrderBy(c => c.Name, StringComparer.CurrentCulture).ToList();
    }

    private static bool IsEmpty(EndOfDaySales sales) => sales.Groups.All(g => g.Count == 0);

    private static List<Guid> Distinct(Guid[]? ids) => (ids ?? []).Distinct().ToList();

    private static void AddTooMany(List<string> invalid, int count, string field)
    {
        if (count > ReportFilterResolver.MaxChoices)
        {
            invalid.Add(field);
        }
    }

    private static List<string> Names<T>(IEnumerable<T> values) where T : struct, Enum => values.Select(v => v.ToString()).ToList();

    private static EndOfDayConcern ParseConcern(string? value, List<string> invalid)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return EndOfDayConcern.Sales;
        }

        if (Enum.TryParse<EndOfDayConcern>(value.Trim(), true, out var concern) && Enum.IsDefined(concern) && !value.Trim().All(char.IsDigit))
        {
            return concern;
        }

        invalid.Add(ConcernField);
        return EndOfDayConcern.Sales;
    }

    public static string ConcernName(EndOfDayConcern concern) => concern.ToString().ToLowerInvariant();

    /// <summary>The names of the values in any letter case (never a number); a name that is none of them names the field as invalid.</summary>
    internal static List<T> ParseEnums<T>(string[]? values, string field, List<string> invalid) where T : struct, Enum
    {
        var parsed = new List<T>();
        foreach (var value in (values ?? []).Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            if (Enum.TryParse<T>(value.Trim(), true, out var item) && Enum.IsDefined(item) && !value.Trim().All(char.IsDigit))
            {
                if (!parsed.Contains(item))
                {
                    parsed.Add(item);
                }
            }
            else if (!invalid.Contains(field))
            {
                invalid.Add(field);
            }
        }

        if (parsed.Count > ReportFilterResolver.MaxChoices)
        {
            invalid.Add(field);
        }

        return parsed;
    }
}
