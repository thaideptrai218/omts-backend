using System.Net;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Authorization;

/// <summary>
/// Owner decision of 20/11/2026 (reverses 27/10 and 02/11): selling is not a Zone Manager's work. Every sales screen answers 403 to a
/// Zone Manager, whatever its zone; the Owner's rights come from the account, not from the matrix, so they are unchanged.
/// </summary>
public sealed class ZoneManagerSalesRightsRemovedTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Theory]
    [InlineData("/api/sales-orders")]
    [InlineData("/api/sales-invoices")]
    [InlineData("/api/sales-returns")]
    [InlineData("/api/customers")]
    [InlineData("/api/quality-claims")]
    [InlineData("/api/purchase-requests")]
    public async Task A_zone_manager_of_a_zone_or_of_all_zones_gets_403_on_the_sales_screens(string url)
    {
        var zone = await factory.AddZoneAsync(NewCode());
        foreach (var grantedZone in new[] { zone, null })
        {
            var account = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
            await factory.GrantAsync(account, StaffRole.ZoneManager, grantedZone);
            var manager = await factory.LoginAsync(account.Username);

            Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(url)).StatusCode);
        }
    }
}
