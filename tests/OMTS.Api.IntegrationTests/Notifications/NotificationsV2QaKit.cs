using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Api.IntegrationTests.Notifications;

/// <summary>
/// Helpers of the QA tests of notifications v2 (OMTS-216 PR 0.5): dispatch as a module does inside its use case, and the
/// ACCOUNT-07/08 endpoints of the caller.
/// </summary>
public static class NotificationsV2QaKit
{
    public const string ListPath = "/api/me/notifications";
    public const string PreferencesPath = "/api/me/notification-preferences";

    /// <summary>Dispatch and save in one scope, as a module does inside its use case.</summary>
    public static async Task NotifyAsync(this WebApplicationFactory<Program> api, IReadOnlyCollection<Guid> recipients, NotificationContent content)
    {
        using var scope = api.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<INotificationDispatcher>().NotifyAsync(recipients, content, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
    }

    /// <summary>A WATER_MEASUREMENT_OVERDUE notification (normal type) that opens the zone (InZone WATER_MEASUREMENTS Create).</summary>
    public static NotificationContent OverdueReminder(FarmingZone zone) => new(
        NotificationTypes.WaterMeasurementOverdue,
        "Water measurement overdue",
        UserMessages.WaterMeasurementOverdue(zone.Name, new DateOnly(2026, 10, 1)),
        new RelatedRecord(RelatedRecordTypes.FarmingZone, zone.Id, zone.Code));

    public static async Task<JsonElement> NotificationPageAsync(this HttpClient client, string query = "")
    {
        var response = await client.GetAsync($"{ListPath}{query}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static IReadOnlyList<JsonElement> Items(this JsonElement page) => page.GetProperty("items").EnumerateArray().ToList();

    public static IReadOnlyList<Guid> NotificationIds(this JsonElement page) =>
        page.Items().Select(i => i.GetProperty("id").GetGuid()).ToList();

    public static Task<HttpResponseMessage> OpenNotificationAsync(this HttpClient client, Guid id) => client.GetAsync($"{ListPath}/{id}/open");

    public static Task<HttpResponseMessage> MarkNotificationReadAsync(this HttpClient client, Guid id) => client.PostAsync($"{ListPath}/{id}/read", null);

    public static Task<HttpResponseMessage> MarkAllNotificationsReadAsync(this HttpClient client) => client.PostAsync($"{ListPath}/read-all", null);

    public static Task<HttpResponseMessage> SavePushAsync(this HttpClient client, params (string Type, bool Push)[] items) =>
        client.PutAsJsonAsync(PreferencesPath, new { items = items.Select(i => new { type = i.Type, push = i.Push }) });

    /// <summary>The offered types in order, each with (mandatory, push).</summary>
    public static async Task<Dictionary<string, (bool Mandatory, bool Push)>> OfferedTypesAsync(this HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>(PreferencesPath)).GetProperty("types").EnumerateArray()
            .ToDictionary(t => t.GetProperty("type").GetString()!, t => (t.GetProperty("mandatory").GetBoolean(), t.GetProperty("push").GetBoolean()));

    /// <summary>The problem body (buffered, so it can be read again).</summary>
    public static async Task<JsonElement> ProblemAsync(this HttpResponseMessage response)
    {
        await response.Content.LoadIntoBufferAsync();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }
}
