using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Sales.PurchaseRequests;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>A logged-in trader with the profile registration links to the account.</summary>
public sealed record TraderActor(HttpClient Client, Account Account, Customer Profile);

/// <summary>SALE-01 to SALE-03 helpers: the routes of a request, and what they leave in the database.</summary>
public static class PurchaseRequestTestKit
{
    private const string Mine = "/api/me/purchase-requests";

    /// <summary>Read when called, never at test discovery: a run may cross midnight in UTC+7.</summary>
    public static DateOnly Today => TimeProvider.System.Today();

    public static async Task<TraderActor> TraderAsync(this AuthApiFactory factory, string? name = null, Action<Customer>? configure = null)
    {
        var (account, profile) = await factory.LinkedProfileAsync(name, configure);
        return new TraderActor(await factory.LoginAsync(account.Username), account, profile);
    }

    public static Task<HttpResponseMessage> PostRequestAsync(this HttpClient trader, Guid? productId, decimal? kg = 100m, DateOnly? pickupDate = null, string? note = null) =>
        trader.PostAsJsonAsync(Mine, new { productId, requestedKg = kg, pickupDate = pickupDate ?? Today.AddDays(2), note });

    /// <summary>Places a request and answers its view.</summary>
    public static async Task<JsonElement> PlaceAsync(this HttpClient trader, Guid productId, decimal kg = 100m, DateOnly? pickupDate = null, string? note = null) =>
        (await (await trader.PostRequestAsync(productId, kg, pickupDate, note)).ReadAsync(HttpStatusCode.Created)).GetProperty("request");

    public static Task<HttpResponseMessage> AcceptAsync(this HttpClient owner, JsonElement request, decimal? kg = null, DateOnly? pickupDate = null, bool? payLater = false, string? note = null) =>
        owner.PostAsJsonAsync($"/api/purchase-requests/{request.Id()}/accept", new
        {
            acceptedKg = kg ?? request.GetProperty("requestedKg").GetDecimal(),
            pickupDate = pickupDate ?? Today.AddDays(2),
            payLaterAgreed = payLater,
            note,
            rowVersion = request.RowVersion(),
        });

    public static Task<HttpResponseMessage> DeclineAsync(this HttpClient owner, JsonElement request, string? note = "Not enough oysters this week") =>
        owner.PostAsJsonAsync($"/api/purchase-requests/{request.Id()}/decline", new { note, rowVersion = request.RowVersion() });

    public static Task<HttpResponseMessage> CancelAsync(this HttpClient trader, JsonElement request, string? reason = null) =>
        trader.PostAsJsonAsync($"{Mine}/{request.Id()}/cancel", new { reason, rowVersion = request.RowVersion() });

    /// <summary>The request as its trader reads it now, with its current row version.</summary>
    public static async Task<JsonElement> MineAsync(this HttpClient trader, Guid requestId) =>
        await (await trader.GetAsync($"{Mine}/{requestId}")).ReadAsync(HttpStatusCode.OK);

    /// <summary>The request as the Owner or a Zone Manager reads it now.</summary>
    public static async Task<JsonElement> ViewedByStaffAsync(this HttpClient staff, Guid requestId) =>
        await (await staff.GetAsync($"/api/purchase-requests/{requestId}")).ReadAsync(HttpStatusCode.OK);

    public static T GetFromJson<T>(this JsonElement element) => element.Deserialize<T>()!;

    /// <summary>The fields a 400 MSG-GEN-01 points out.</summary>
    public static async Task<List<string>> InvalidFieldsAsync(this HttpResponseMessage response)
    {
        var body = await response.ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        return body.TryGetProperty("fields", out var fields)
            ? fields.EnumerateArray().Select(f => f.GetString()!).ToList()
            : [body.GetProperty("field").GetString()!];
    }

    public static Task<HttpResponseMessage> PostAsJsonRawAsync(this HttpClient client, string path, string json) =>
        client.PostAsync(path, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

    public static Task<HttpResponseMessage> PutAsJsonAsyncRaw(this HttpClient client, string path, string json) =>
        client.PutAsync(path, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

    public static async Task<T> DbAsync<T>(this WebApplicationFactory<Program> api, Func<OmtsDbContext, Task<T>> query)
    {
        using var scope = api.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<OmtsDbContext>());
    }

    /// <summary>Every Notification row of a request, list rows and sendings.</summary>
    public static Task<List<Notification>> NotificationRowsAsync(this WebApplicationFactory<Program> api, Guid requestId) =>
        api.DbAsync(db => db.Notifications.AsNoTracking().Where(n => n.RelatedRecordId == requestId).OrderBy(n => n.CreatedAt).ToListAsync());

    public static Task<List<AuditEvent>> AuditRowsAsync(this WebApplicationFactory<Program> api, Guid requestId) =>
        api.DbAsync(db => db.AuditEvents.AsNoTracking().Where(e => e.RecordType == nameof(PurchaseRequest) && e.RecordId == requestId).ToListAsync());

    /// <summary>Makes the fake SMS sender refuse this number, as the service would (<c>Sms:Fake:FailFor</c>).</summary>
    public static void RefuseSmsTo(this WebApplicationFactory<Program> api, string phoneNumber) =>
        api.Services.GetRequiredService<IOptions<SmsOptions>>().Value.Fake.FailFor.Add(phoneNumber);

    public static void RefusePushTo(this WebApplicationFactory<Program> api, Guid accountId) =>
        api.Services.GetRequiredService<IOptions<PushOptions>>().Value.Fake.FailFor.Add(accountId.ToString());

    public static List<Notification> InList(this IEnumerable<Notification> rows) => rows.Where(n => n.Channel == NotificationChannel.InApp).ToList();

    public static List<Notification> Sendings(this IEnumerable<Notification> rows) => rows.Where(n => n.Channel != NotificationChannel.InApp).ToList();

    public static IEnumerable<Notification> OfType(this IEnumerable<Notification> rows, string type) => rows.Where(n => n.Type == type);
}

/// <summary>The host with the two seams a test sets: the trader message sender, and the hook SALE-05 will fill.</summary>
public sealed class PurchaseRequestStubsFactory : AuthApiFactory
{
    public StubTraderMessageSender Sender { get; } = new();

    public StubFulfilment Fulfilment { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ITraderMessageSender>(Sender);
            services.AddSingleton<IPurchaseRequestFulfilment>(Fulfilment);
        });
    }
}

public sealed class StubTraderMessageSender : ITraderMessageSender
{
    public Func<CancellationToken, Task<DeliveryResult>> Send { get; set; } = _ => Task.FromResult(DeliveryResult.Sent("stub"));

    public NotificationChannel Channel => NotificationChannel.Sms;

    public Task<DeliveryResult> SendAsync(string phoneNumber, string text, CancellationToken cancellationToken = default) => Send(cancellationToken);
}

public sealed class StubFulfilment : IPurchaseRequestFulfilment
{
    public HashSet<Guid> Fulfilled { get; } = [];

    /// <summary>Runs once inside the next check: after the cancel read the row and before it saves, so that the race has one outcome.</summary>
    public Func<Task>? DuringNextCheck { get; set; }

    public async Task<IReadOnlySet<Guid>> FulfilledAmongAsync(IReadOnlyCollection<Guid> requestIds, CancellationToken cancellationToken)
    {
        if (DuringNextCheck is { } action)
        {
            DuringNextCheck = null;
            await action();
        }

        return Fulfilled.Intersect(requestIds).ToHashSet();
    }
}
