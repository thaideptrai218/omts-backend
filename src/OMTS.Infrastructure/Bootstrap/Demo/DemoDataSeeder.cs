using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Application.Common;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// Demo data of an oyster cooperative in Vân Đồn, Quảng Ninh (OMTS-213). Runs only from
/// <c>seed --bootstrap --demo</c>, where <see cref="DemoSeedGate"/> decides. Data is created through the same
/// application services the API uses, acting as the Cooperative Owner (<see cref="AmbientActor"/>), so
/// validation, VND rounding, statuses, row versions and audit rows match real use. Idempotent: every step
/// finds its rows by business code. The labour modules get their own steps later.
/// </summary>
internal static class DemoDataSeeder
{
    // Order matters: later steps need the zones, accounts, suppliers and items of earlier ones (the water alerts go
    // to the zone staff created before them).
    private static readonly IDemoSeedStep[] Steps =
    [
        new DemoCooperativeProfileStep(),
        new DemoFarmingZoneStep(),
        new DemoZoneLicenseStep(),
        new DemoRaftStep(),
        new DemoStaffAndEmployeeStep(),
        new DemoTraderStep(),
        new DemoProductStep(),
        new DemoCustomerStep(),
        new DemoSupplierStep(),
        new DemoInputItemStep(),
        new DemoPurchaseReceiptStep(),
        new DemoWaterMeasurementStep(),
    ];

    public static async Task<IReadOnlyList<string>> SeedAsync(
        IServiceProvider services, string? ownerUsername, Guid cooperativeId, string password, bool ownerLoginMayBeSet, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<OmtsDbContext>();
        var owners = db.Accounts.Where(a => a.SubjectType == SubjectType.CooperativeOwner);
        var owner = (ownerUsername is { Length: > 0 } ? await owners.FirstOrDefaultAsync(a => a.Username == ownerUsername, cancellationToken) : null)
            ?? await owners.OrderBy(a => a.CreatedAt).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Demo data needs the Cooperative Owner created by the bootstrap.");
        var context = new DemoSeedContext(services, db, cooperativeId, owner.Id, password, ownerLoginMayBeSet, services.GetRequiredService<TimeProvider>().GetUtcNow());

        var messages = new List<string>();
        // A system action like the bootstrap itself: no acting account yet.
        messages.Add(Describe("owner login", await new DemoOwnerStep().RunAsync(context, cancellationToken)));

        // From here on the services see the Owner as the logged-in user.
        using var acting = services.GetRequiredService<AmbientActor>().Use(owner.Id);
        foreach (var step in Steps)
        {
            messages.Add(Describe(step.Name, await step.RunAsync(context, cancellationToken)));
        }

        return messages;
    }

    private static string Describe(string name, int created) =>
        created == 0 ? $"Demo {name}: already there." : $"Demo {name}: {created} created.";
}
