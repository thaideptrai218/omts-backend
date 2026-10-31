using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Audit;

/// <summary>
/// QA of OMTS-216 PR 0.6: the record resolver now shared by ACCOUNT-08 and ACCOUNT-10.1 finds the record of every type
/// the audit log and the notifications write, with the zone its access check uses, and finds nothing for an unknown,
/// retired or removed type (MSG-ACCOUNT-22).
/// </summary>
public class AuditRecordResolverQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Every_record_type_written_to_the_log_resolves_with_the_zone_of_its_access_check()
    {
        var cooperative = await factory.EnsureCooperativeAsync();
        var zone = await factory.AddZoneAsync(NewCode());
        var account = await factory.AddAccountAsync(NewCode(), NewPassword(), SubjectType.Staff);
        var zoneGrant = await factory.GrantAsync(account, StaffRole.ZoneManager, zone);
        var allZonesGrant = await factory.GrantAsync(account, StaffRole.Accountant, null);
        var role = await factory.RoleAsync(StaffRole.ZoneManager);
        var raft = new Raft { ZoneId = zone.Id, Code = $"{zone.Code}-B01" };
        var measurement = new WaterMeasurement { ZoneId = zone.Id, MeasuredAt = DateTimeOffset.UtcNow };
        var license = new ZoneComplianceDocument { DocumentType = "AQUACULTURE_LICENSE", DocumentNumber = NewCode() };
        var employee = new Employee { CooperativeId = cooperative.Id, FullName = "Lê Thị Hoa" };
        var supplier = new Supplier { Name = "NCC", PhoneNumber = NewPhone() };
        var item = new FarmingInputItem { Code = NewCode(), Name = "Phao", Unit = "cái" };
        var receipt = new PurchaseReceipt { Code = NewCode(), SupplierId = supplier.Id, ZoneId = zone.Id };
        var purchaseReturn = new PurchaseReturn { Code = NewCode(), ReceiptId = receipt.Id, Reason = "Hỏng" };
        var payment = new SupplierPayment { SupplierId = supplier.Id, Amount = 1 };
        var device = new PushDevice { AccountId = account.Id, Token = NewCode(), Platform = "android" };
        await factory.UseDbAsync(db => db.AddRange(raft, measurement, license, employee, supplier, item, receipt, purchaseReturn, payment, device));

        var expected = new (string Type, Guid Id, Guid? Zone)[]
        {
            (RelatedRecordTypes.FarmingZone, zone.Id, zone.Id),
            (nameof(Raft), raft.Id, zone.Id),
            (RelatedRecordTypes.WaterMeasurement, measurement.Id, zone.Id),
            (nameof(PurchaseReceipt), receipt.Id, zone.Id),
            (nameof(AccessGrant), zoneGrant.Id, zone.Id),
            // Records with no single zone: Owner-only, or checked by role only (BR-FARM-08).
            (nameof(AccessGrant), allZonesGrant.Id, null),
            (RelatedRecordTypes.ZoneLicense, license.Id, null),
            (RelatedRecordTypes.Account, account.Id, null),
            (nameof(Cooperative), cooperative.Id, null),
            (nameof(Employee), employee.Id, null),
            (nameof(Role), role.Id, null),
            (nameof(Supplier), supplier.Id, null),
            (nameof(FarmingInputItem), item.Id, null),
            (nameof(PurchaseReturn), purchaseReturn.Id, null),
            (nameof(SupplierPayment), payment.Id, null),
            (nameof(PushDevice), device.Id, null),
        };

        foreach (var (type, id, zoneId) in expected)
        {
            var found = await FindAsync(type, id);
            Assert.True(found is not null, $"{type} {id} was not found.");
            Assert.True(found!.ZoneId == zoneId, $"{type}: zone {found.ZoneId}, expected {zoneId}.");
        }
    }

    [Fact]
    public async Task Unknown_retired_removed_and_non_entity_types_resolve_to_nothing()
    {
        var zone = await factory.AddZoneAsync(NewCode());

        Assert.NotNull(await FindAsync(RelatedRecordTypes.FarmingZone, zone.Id));
        foreach (var type in new[] { "RetiredThing", "farmingzone", "FarmingZones", "", " ", "Status", "NotificationPreference", "BankAccountInfo" })
        {
            Assert.True(await FindAsync(type, zone.Id) is null, $"\"{type}\" resolved.");
        }

        await factory.UseDbAsync(db => db.FarmingZones.Remove(db.FarmingZones.Single(z => z.Id == zone.Id)));
        Assert.Null(await FindAsync(RelatedRecordTypes.FarmingZone, zone.Id));
    }

    // MSG-ACCOUNT-22 {object_name}: the words of the record type, as the PR 0.5 names were.
    [Theory]
    [InlineData("ZoneLicense", "Zone license")]
    [InlineData("WaterMeasurement", "Water measurement")]
    [InlineData("FarmingZone", "Farming zone")]
    [InlineData("Account", "Account")]
    [InlineData("PurchaseReceipt", "Purchase receipt")]
    public void The_object_name_of_a_record_type_is_its_words(string type, string name) =>
        Assert.Equal(name, RelatedRecordTypes.ObjectName(type));

    private async Task<RelatedRecordLocation?> FindAsync(string type, Guid id)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IRelatedRecordQuery>().FindAsync(type, id, CancellationToken.None);
    }
}
