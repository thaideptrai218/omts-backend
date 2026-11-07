using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// QA of PR P2 (OMTS-218), PURCHASE-01 steps 8–9: the receipt, its audit and the Owner's MSG-PURCHASE-01 (POST-1, POST-3)
/// are one save. A second host shares the in-memory database and fails every save that holds a new MSG-PURCHASE-01 row, as
/// a database error would; the first host sets the data up and reads what was kept.
/// </summary>
public class PurchaseReceiptAtomicSaveQaTests
{
    // QA P2 F1 (Medium): CreateAsync saves the receipt as Completed (first save, which also gives the PN code) and adds the
    // notification in a second save. When that second save fails, the caller gets a 500 although the receipt is Completed:
    // it counts in the supplier debt with no MSG-PURCHASE-01, and the client, told it failed, records it again (a second
    // Completed receipt, the debt counted twice). Fix: one save (allocate the PN number first, e.g. a SQL sequence, and
    // build the notification before saving; or retry the code inside one transaction holding every row).
    [Fact]
    public async Task Creating_and_completing_in_one_call_keeps_nothing_when_the_owner_notification_cannot_be_saved()
    {
        await using var factory = new AuthApiFactory();
        var (world, username, password) = await WorldWithOwnerAsync(factory);
        await using var failing = factory.WithWebHostBuilder(b => b.ConfigureTestServices(FailCompletedNotificationSaves));
        var owner = await LoginAsync(failing, username, password);

        var response = await owner.PostReceiptAsync(world.Body(new LineBody(world.Other, 2, 150_000)) with { Complete = true });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var kept = await factory.QueryAsync(db => db.PurchaseReceipts.Where(r => r.SupplierId == world.SupplierId).Select(r => new { r.Id, r.StatusId }).ToListAsync());
        var notified = await factory.QueryAsync(db => db.Notifications.Where(n => n.Type == NotificationTypes.PurchaseReceiptCompleted)
            .Select(n => n.RelatedRecordId).ToListAsync());
        // All or nothing: a refused call leaves no Completed receipt without its notification to the Owner.
        Assert.DoesNotContain(kept, r => r.StatusId == StatusIds.PurchaseReceipt.Completed && !notified.Contains(r.Id));
    }

    // The two-step path (Save draft, then Complete) is one save: the draft stays a Draft, nothing is half done.
    [Fact]
    public async Task Completing_a_saved_draft_keeps_the_draft_when_the_owner_notification_cannot_be_saved()
    {
        await using var factory = new AuthApiFactory();
        var (world, username, password) = await WorldWithOwnerAsync(factory);
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 2, 150_000)));
        await using var failing = factory.WithWebHostBuilder(b => b.ConfigureTestServices(FailCompletedNotificationSaves));
        var owner = await LoginAsync(failing, username, password);

        var response = await owner.CompleteAsync(draft.Id());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(StatusIds.PurchaseReceipt.Draft, await factory.QueryAsync(db => db.PurchaseReceipts.Where(r => r.Id == draft.Id()).Select(r => r.StatusId).SingleAsync()));
        Assert.DoesNotContain("PurchaseReceipt.StatusChanged", await factory.AuditActionsAsync(draft.Id()));
        // Once the database works again, the same Complete goes through with its notification.
        await (await world.Owner.CompleteAsync(draft.Id())).ReadAsync(HttpStatusCode.OK);
        Assert.True(await factory.QueryAsync(db => db.Notifications.AnyAsync(n => n.RelatedRecordId == draft.Id() && n.Type == NotificationTypes.PurchaseReceiptCompleted)));
    }

    private static async Task<(PurchasingWorld World, string Username, string Password)> WorldWithOwnerAsync(AuthApiFactory factory)
    {
        await factory.EnsureCooperativeAsync();
        var password = NewPassword();
        var account = await factory.AddAccountAsync(NewCode(), password);
        var world = await factory.WorldAsync(await factory.LoginAsync(account.Username, password));
        return (world, account.Username, password);
    }

    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> host, string username, string password)
    {
        var client = host.CreateClient();
        var tokens = await LoginTokensAsync(client, username, password);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    /// <summary>Wraps the real unit of work so that a save holding a new MSG-PURCHASE-01 row fails.</summary>
    private static void FailCompletedNotificationSaves(IServiceCollection services)
    {
        var real = services.Last(d => d.ServiceType == typeof(IUnitOfWork));
        services.Remove(real);
        services.AddScoped<IUnitOfWork>(provider => new CompletedNotificationSaveFails(
            (IUnitOfWork)ActivatorUtilities.CreateInstance(provider, real.ImplementationType!), provider.GetRequiredService<OmtsDbContext>()));
    }

    private sealed class CompletedNotificationSaveFails(IUnitOfWork inner, OmtsDbContext db) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            db.ChangeTracker.Entries<Notification>().Any(e => e.State == EntityState.Added && e.Entity.Type == NotificationTypes.PurchaseReceiptCompleted)
                ? throw new DbUpdateException("QA fault injection: the notification rows could not be saved.")
                : inner.SaveChangesAsync(cancellationToken);

        public Task ReloadAsync(object entity, CancellationToken cancellationToken = default) => inner.ReloadAsync(entity, cancellationToken);

        public void DiscardChanges() => inner.DiscardChanges();

        public void ExpectRowVersion(AuditableEntity entity, byte[] rowVersion) => inner.ExpectRowVersion(entity, rowVersion);

        public void MarkChanged(AuditableEntity entity) => inner.MarkChanged(entity);
    }
}
