using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>Shared set-up of the QA tests of employees v2 (OMTS-227): callers with mixed pairs and a profile with every personal field.</summary>
internal static class EmployeesV2QaKit
{
    /// <summary>The fields that a caller who does not maintain the employee gets as null.</summary>
    public static readonly string[] PersonalFields = ["birthday", "address", "area", "ward", "otherInformation", "notes", "bankAccount"];

    /// <summary>A client of a staff account holding the pairs: (role, zone), null zone being All zones.</summary>
    public static async Task<(HttpClient Client, Account Account)> StaffAsync(AuthApiFactory factory, params (StaffRole Role, FarmingZone? Zone)[] pairs)
    {
        var password = NewPassword();
        var account = await factory.AddAccountAsync(NewCode(), password, SubjectType.Staff);
        foreach (var (role, zone) in pairs)
        {
            await factory.GrantAsync(account, role, zone);
        }

        return (await factory.LoginAsync(account.Username, password), account);
    }

    public static Dictionary<string, object?> FullProfile(string name, string? phone, params FarmingZone[] zones) =>
        Profile(name, phone, zones[0].Id, b =>
        {
            b["workingZoneIds"] = zones.Select(z => z.Id).ToArray();
            b["address"] = "Riêng tư địa chỉ";
            b["area"] = "Riêng tư khu";
            b["ward"] = "Riêng tư xã";
            b["otherInformation"] = "Riêng tư khác";
            b["notes"] = "Riêng tư ghi chú";
            b["bankAccount"] = Bank("999900000099");
        });

    public static void AssertPersonalFields(JsonElement view, bool visible)
    {
        foreach (var field in PersonalFields)
        {
            Assert.True(
                (view.GetProperty(field).ValueKind != JsonValueKind.Null) == visible,
                $"{field} is {view.GetProperty(field).ValueKind}, expected {(visible ? "a value" : "null")}.");
        }
    }

    /// <summary>The digits written with another Unicode digit block (zero at <paramref name="zero"/>).</summary>
    public static string InDigits(string phone, char zero) => string.Concat(phone.Select(c => (char)(zero + (c - '0'))));

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response, HttpStatusCode status)
    {
        await response.Content.LoadIntoBufferAsync();
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    public static Task<HttpResponseMessage> PostEndAsync(this HttpClient client, Guid id, object body) =>
        client.PostAsJsonAsync($"/api/employees/{id}/end-employment", body);
}
