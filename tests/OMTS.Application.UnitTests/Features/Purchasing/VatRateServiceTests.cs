using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Purchasing.Items;
using OMTS.Application.UnitTests.Fakes;
using OMTS.Application.UnitTests.Features.Authorization;
using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Purchasing;

/// <summary>The VAT rate list: KCT is locked, one default, a rate that items still have is deactivated only with a replacement and its items move, every change is audited.</summary>
public sealed class VatRateServiceTests
{
    private static readonly byte[] Version = [1, 2, 3, 4, 5, 6, 7, 8];

    private sealed class FakeVatRates : IVatRateRepository
    {
        public List<VatRate> Rates { get; } = VatKit.Catalogue().Values.ToList();

        public List<FarmingInputItem> Items { get; } = [];

        public Task<IReadOnlyList<VatRate>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VatRate>>(Rates.OrderByDescending(r => r.IsActive).ThenBy(r => r.SortOrder).ToList());

        public Task<IReadOnlyDictionary<Guid, VatRate>> CatalogueAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, VatRate>>(Rates.ToDictionary(r => r.Id));

        public Task<VatRate?> GetForEditAsync(Guid rateId, CancellationToken cancellationToken) => Task.FromResult(Rates.FirstOrDefault(r => r.Id == rateId));

        public Task<IReadOnlyList<VatRate>> GetAllForEditAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VatRate>>(Rates.OrderBy(r => r.SortOrder).ToList());

        public Task<VatRate?> FindByNameAsync(string name, Guid? exceptRateId, CancellationToken cancellationToken) =>
            Task.FromResult(Rates.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase) && r.Id != exceptRateId));

        public Task<IReadOnlyDictionary<Guid, int>> ItemCountsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, int>>(Items.Where(i => !i.IsHidden).GroupBy(i => i.PurchaseVatRateId).ToDictionary(g => g.Key, g => g.Count()));

        public Task<IReadOnlyList<FarmingInputItem>> ItemsOfAsync(Guid rateId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FarmingInputItem>>(Items.Where(i => i.PurchaseVatRateId == rateId).ToList());

        public void Add(VatRate rate) => Rates.Add(rate);
    }

    private sealed class Setup
    {
        public Setup()
        {
            foreach (var rate in Rates.Rates)
            {
                rate.RowVersion = Version;
            }
        }

        public FakeVatRates Rates { get; } = new();

        public FakeAuditEventRepository Audit { get; } = new();

        public int Saves { get; private set; }

        public VatRate Rate(Guid id) => Rates.Rates.Single(r => r.Id == id);

        public FarmingInputItem ItemOn(Guid rateId, bool hidden = false)
        {
            var item = new FarmingInputItem { Group = InputItemGroup.Material, PurchaseVatRateId = rateId, IsHidden = hidden };
            Rates.Items.Add(item);
            return item;
        }

        public VatRateService Service(AccountAccess? reader = null)
        {
            var unitOfWork = StubProxy.Create<IUnitOfWork>(h =>
            {
                h["SaveChangesAsync"] = _ =>
                {
                    Saves++;
                    return 1;
                };
                h["MarkChanged"] = _ => null;
                h["ExpectRowVersion"] = _ => null;
                h["DiscardChanges"] = _ => null;
            });
            var catalog = new FakeStatusCatalog();
            var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 28, 3, 0, 0, TimeSpan.Zero));
            var access = new InputItemAccess(ZoneAccessKit.Guard(reader ?? ZoneAccessKit.Owner()));
            return new VatRateService(
                Rates, access, new RowVersionGuard(StubProxy.Create<IAccountRepository>(_ => { }), unitOfWork),
                new AuditLogService(Audit, new FakeCurrentUser(Guid.NewGuid()), catalog, clock), unitOfWork);
        }
    }

    private static string Current => RowVersionGuard.Encode(Version);

    private static string? ReasonOf(AppError error) => error.Details?.GetValueOrDefault("reasonCode") as string;

    // ---- reading ----

    [Fact]
    public async Task The_list_has_active_rates_first_with_their_item_counts_and_can_be_cut_to_the_active_ones()
    {
        var setup = new Setup();
        setup.ItemOn(VatRateIds.Ten);
        setup.ItemOn(VatRateIds.Ten);
        setup.ItemOn(VatRateIds.Ten, hidden: true);
        setup.Rate(VatRateIds.Zero).IsActive = false;

        var all = (await setup.Service().ListAsync(null, default)).Value!;
        var active = (await setup.Service().ListAsync(true, default)).Value!;

        Assert.Equal(["KCT", "5%", "8%", "10%", "0%"], all.Select(v => v.Name));
        Assert.Equal(["KCT", "5%", "8%", "10%"], active.Select(v => v.Name));
        Assert.Equal(2, all.Single(v => v.Name == "10%").ItemCount);
        var kct = all.First();
        Assert.Equal((null, true, true, false), (kct.Percent, kct.IsExempt, kct.IsSystem, kct.IsDefault));
        Assert.True(all.Single(v => v.Name == "10%").IsDefault);
    }

    [Fact]
    public async Task Whoever_views_purchase_receipts_may_read_the_rates_and_a_caller_with_no_such_right_may_not()
    {
        var setup = new Setup();

        var viewer = await setup.Service(ZoneAccessKit.Viewer(PermissionFunctions.PurchaseReceipts, Guid.NewGuid())).ListAsync(null, default);
        var stranger = await setup.Service(ZoneAccessKit.Viewer(PermissionFunctions.Customers, Guid.NewGuid())).ListAsync(null, default);

        Assert.True(viewer.IsSuccess);
        Assert.Equal(ErrorKind.Forbidden, stranger.Error!.Kind);
    }

    // ---- create and edit ----

    [Fact]
    public async Task A_new_rate_goes_to_the_end_of_the_list_and_is_audited()
    {
        var setup = new Setup();

        var result = await setup.Service().CreateAsync(new SaveVatRateRequest(" 3,5% ", 3.5m), default);

        var view = result.Value!;
        Assert.Equal(("3,5%", 3.5m, false, false, true, 0), (view.Name, view.Percent, view.IsExempt, view.IsSystem, view.Active, view.ItemCount));
        Assert.Equal(5, view.SortOrder);
        var audit = Assert.Single(setup.Audit.Added);
        Assert.Equal(("VatRate.Created", "VatRate", "3,5%"), (audit.Action, audit.RecordType, audit.RecordCode));
        Assert.Contains("3.5", audit.After);
    }

    [Theory]
    [InlineData("", 5d, "Name")]
    [InlineData("   ", 5d, "Name")]
    [InlineData("A very long name that goes on past the fifty characters allowed", 5d, "Name")]
    [InlineData("6%", null, "Percent")]
    [InlineData("6%", -1d, "Percent")]
    [InlineData("6%", 100.01d, "Percent")]
    [InlineData("6%", 5.123d, "Percent")]
    public async Task A_rate_needs_a_name_of_1_to_50_characters_and_a_percent_from_0_to_100_with_two_decimals(string name, double? percent, string field)
    {
        var setup = new Setup();

        var result = await setup.Service().CreateAsync(new SaveVatRateRequest(name, percent is null ? null : (decimal)percent), default);

        Assert.Equal(field, result.Error!.Details!["field"]);
        Assert.Empty(setup.Audit.Added);
        Assert.Equal(0, setup.Saves);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(100d)]
    [InlineData(7.25d)]
    public async Task The_percent_may_be_0_100_or_have_two_decimals(double percent) =>
        Assert.True((await new Setup().Service().CreateAsync(new SaveVatRateRequest("Mức mới", (decimal)percent), default)).IsSuccess);

    [Fact]
    public async Task A_name_is_unique_regardless_of_letter_case()
    {
        var setup = new Setup();

        var result = await setup.Service().CreateAsync(new SaveVatRateRequest("kct", 1m), default);

        Assert.Equal(ErrorKind.Conflict, result.Error!.Kind);
        Assert.Equal(VatRateIds.Kct, result.Error.Details!["existingId"]);
        Assert.Equal("Name", result.Error.Details["field"]);
        Assert.Equal(5, setup.Rates.Rates.Count);
    }

    [Fact]
    public async Task Editing_the_percent_audits_the_percent_before_and_after_and_a_name_that_is_taken_is_refused()
    {
        var setup = new Setup();
        var ten = setup.Rate(VatRateIds.Ten);

        var result = await setup.Service().UpdateAsync(ten.Id, new SaveVatRateRequest("10%", 8.5m, Current), default);

        Assert.Equal(8.5m, result.Value!.Percent);
        Assert.Equal(8.5m, ten.Percent);
        var audit = Assert.Single(setup.Audit.Added);
        Assert.Equal("VatRate.Updated", audit.Action);
        Assert.Contains("10", audit.Before);
        Assert.Contains("8.5", audit.After);

        var taken = await setup.Service().UpdateAsync(ten.Id, new SaveVatRateRequest("8%", 8m, Current), default);
        Assert.Equal(setup.Rate(VatRateIds.Eight).Id, taken.Error!.Details!["existingId"]);
        Assert.Single(setup.Audit.Added);
    }

    [Fact]
    public async Task Saving_a_rate_without_a_change_writes_no_audit_row()
    {
        var setup = new Setup();

        var result = await setup.Service().UpdateAsync(VatRateIds.Five, new SaveVatRateRequest("5%", 5m, Current), default);

        Assert.True(result.IsSuccess);
        Assert.Empty(setup.Audit.Added);
    }

    [Fact]
    public async Task An_edit_made_on_an_old_version_is_refused()
    {
        var setup = new Setup();

        var result = await setup.Service().UpdateAsync(VatRateIds.Five, new SaveVatRateRequest("5%", 6m, RowVersionGuard.Encode([9, 9, 9, 9, 9, 9, 9, 9])), default);

        Assert.Equal(ErrorKind.Conflict, result.Error!.Kind);
        Assert.Equal(5m, setup.Rate(VatRateIds.Five).Percent);
    }

    // ---- KCT is locked ----

    [Fact]
    public async Task KCT_cannot_be_edited_deactivated_or_made_the_default()
    {
        var setup = new Setup();
        var service = setup.Service();

        var edit = await service.UpdateAsync(VatRateIds.Kct, new SaveVatRateRequest("Không thuế", 0m, Current), default);
        var deactivate = await service.DeactivateAsync(VatRateIds.Kct, new DeactivateVatRateRequest(VatRateIds.Ten, Current), default);
        var makeDefault = await service.SetDefaultAsync(VatRateIds.Kct, default);

        Assert.All(new[] { edit.Error!, deactivate.Error!, makeDefault.Error! }, e =>
        {
            Assert.Equal(ErrorKind.Conflict, e.Kind);
            Assert.Equal("MSG-GEN-09", e.Message.Code);
        });
        Assert.Equal(VatRateRules.SystemRate, ReasonOf(edit.Error!));
        Assert.Equal(VatRateRules.SystemRate, ReasonOf(deactivate.Error!));
        Assert.Equal(VatRateRules.ExemptNotDefault, ReasonOf(makeDefault.Error!));
        var kct = setup.Rate(VatRateIds.Kct);
        Assert.Equal(("KCT", null, true), (kct.Name, kct.Percent, kct.IsActive));
        Assert.Empty(setup.Audit.Added);
    }

    // ---- the default ----

    [Fact]
    public async Task Making_another_rate_the_default_leaves_exactly_one_default_and_is_audited()
    {
        var setup = new Setup();

        var result = await setup.Service().SetDefaultAsync(VatRateIds.Eight, default);

        Assert.True(result.Value!.IsDefault);
        Assert.Equal([VatRateIds.Eight], setup.Rates.Rates.Where(r => r.IsDefault).Select(r => r.Id));
        var audit = Assert.Single(setup.Audit.Added);
        Assert.Equal("VatRate.DefaultChanged", audit.Action);
        Assert.Contains("10%", audit.Before);
        Assert.Contains("8%", audit.After);
    }

    [Fact]
    public async Task An_inactive_rate_cannot_become_the_default_and_the_default_cannot_be_deactivated()
    {
        var setup = new Setup();
        setup.Rate(VatRateIds.Five).IsActive = false;

        var inactive = await setup.Service().SetDefaultAsync(VatRateIds.Five, default);
        var deactivate = await setup.Service().DeactivateAsync(VatRateIds.Ten, new DeactivateVatRateRequest(VatRateIds.Eight, Current), default);

        Assert.Equal(VatRateRules.InactiveNotDefault, ReasonOf(inactive.Error!));
        Assert.Equal(VatRateRules.DefaultNotDeactivated, ReasonOf(deactivate.Error!));
        Assert.Equal("MSG-GEN-09", deactivate.Error!.Message.Code);
        Assert.True(setup.Rate(VatRateIds.Ten).IsActive);
        Assert.Empty(setup.Audit.Added);
    }

    // ---- deactivate ----

    [Fact]
    public async Task A_rate_nobody_has_is_deactivated_without_a_replacement()
    {
        var setup = new Setup();

        var result = await setup.Service().DeactivateAsync(VatRateIds.Zero, new DeactivateVatRateRequest(null, Current), default);

        Assert.Equal(0, result.Value!.MovedItems);
        Assert.False(result.Value.Rate.Active);
        var audit = Assert.Single(setup.Audit.Added);
        Assert.Equal("VatRate.Deactivated", audit.Action);
        Assert.Contains("\"MovedItems\":0", audit.After);
    }

    [Fact]
    public async Task A_rate_that_items_have_needs_a_replacement_that_is_active_and_another_rate()
    {
        var setup = new Setup();
        setup.ItemOn(VatRateIds.Eight);
        setup.Rate(VatRateIds.Five).IsActive = false;
        var service = setup.Service();

        var none = await service.DeactivateAsync(VatRateIds.Eight, new DeactivateVatRateRequest(null, Current), default);
        var itself = await service.DeactivateAsync(VatRateIds.Eight, new DeactivateVatRateRequest(VatRateIds.Eight, Current), default);
        var inactive = await service.DeactivateAsync(VatRateIds.Eight, new DeactivateVatRateRequest(VatRateIds.Five, Current), default);
        var unknown = await service.DeactivateAsync(VatRateIds.Eight, new DeactivateVatRateRequest(Guid.NewGuid(), Current), default);

        Assert.All(new[] { none, itself, inactive, unknown }, r =>
        {
            Assert.Equal(ErrorKind.Validation, r.Error!.Kind);
            Assert.Equal("replacementId", r.Error.Details!["field"]);
            Assert.Equal("MSG-GEN-01", r.Error.Message.Code);
        });
        Assert.True(setup.Rate(VatRateIds.Eight).IsActive);
        Assert.All(setup.Rates.Items, i => Assert.Equal(VatRateIds.Eight, i.PurchaseVatRateId));
        Assert.Empty(setup.Audit.Added);
    }

    [Fact]
    public async Task Deactivating_a_rate_moves_its_items_to_the_replacement_and_counts_the_ones_the_Owner_sees()
    {
        var setup = new Setup();
        var one = setup.ItemOn(VatRateIds.Eight);
        var two = setup.ItemOn(VatRateIds.Eight);
        var deleted = setup.ItemOn(VatRateIds.Eight, hidden: true);
        var elsewhere = setup.ItemOn(VatRateIds.Five);

        var result = await setup.Service().DeactivateAsync(VatRateIds.Eight, new DeactivateVatRateRequest(VatRateIds.Ten, Current), default);

        Assert.Equal(2, result.Value!.MovedItems);
        Assert.All(new[] { one, two, deleted }, i => Assert.Equal(VatRateIds.Ten, i.PurchaseVatRateId));
        Assert.Equal(VatRateIds.Five, elsewhere.PurchaseVatRateId);
        Assert.Equal((false, 0), (result.Value.Rate.Active, result.Value.Rate.ItemCount));
        var audit = Assert.Single(setup.Audit.Added);
        Assert.Contains("\"MovedItems\":2", audit.After);
        Assert.Contains("10%", audit.After);
        Assert.Equal(1, setup.Saves);
    }

    [Fact]
    public async Task KCT_may_be_the_replacement_of_a_rate_that_goes()
    {
        var setup = new Setup();
        var item = setup.ItemOn(VatRateIds.Five);

        var result = await setup.Service().DeactivateAsync(VatRateIds.Five, new DeactivateVatRateRequest(VatRateIds.Kct, Current), default);

        Assert.Equal(1, result.Value!.MovedItems);
        Assert.Equal(VatRateIds.Kct, item.PurchaseVatRateId);
    }

    // ---- activate ----

    [Fact]
    public async Task An_inactive_rate_is_activated_again_and_audited_once()
    {
        var setup = new Setup();
        setup.Rate(VatRateIds.Zero).IsActive = false;

        var first = await setup.Service().ActivateAsync(VatRateIds.Zero, default);
        var again = await setup.Service().ActivateAsync(VatRateIds.Zero, default);

        Assert.True(first.Value!.Active);
        Assert.True(again.Value!.Active);
        var audit = Assert.Single(setup.Audit.Added);
        Assert.Equal("VatRate.Activated", audit.Action);
    }

    [Fact]
    public async Task An_unknown_rate_is_not_found_on_every_action()
    {
        var setup = new Setup();
        var service = setup.Service();
        var id = Guid.NewGuid();

        Assert.Equal(ErrorKind.NotFound, (await service.UpdateAsync(id, new SaveVatRateRequest("x", 1m, Current), default)).Error!.Kind);
        Assert.Equal(ErrorKind.NotFound, (await service.SetDefaultAsync(id, default)).Error!.Kind);
        Assert.Equal(ErrorKind.NotFound, (await service.DeactivateAsync(id, new DeactivateVatRateRequest(null, Current), default)).Error!.Kind);
        Assert.Equal(ErrorKind.NotFound, (await service.ActivateAsync(id, default)).Error!.Kind);
    }

    // ---- the rate an item starts with ----

    [Fact]
    public void A_new_supply_item_starts_at_the_active_default_and_never_at_KCT_unless_nothing_else_is_left()
    {
        var catalogue = VatKit.Catalogue();
        Assert.Equal(VatRateIds.Ten, VatRateRules.DefaultOf(catalogue).Id);

        catalogue[VatRateIds.Ten].IsActive = false;
        Assert.Equal(VatRateIds.Zero, VatRateRules.DefaultOf(catalogue).Id);

        foreach (var rate in catalogue.Values.Where(r => !r.IsSystem))
        {
            rate.IsActive = false;
        }

        Assert.Equal(VatRateIds.Kct, VatRateRules.DefaultOf(catalogue).Id);
    }
}
