using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>Request builders for the LAB-01 tests; names and phones are unique so tests can share a database.</summary>
internal static class EmployeeRequests
{
    public static async Task<(HttpClient Owner, FarmingZone Zone, Role Role)> SetupAsync(AuthApiFactory factory)
    {
        await factory.EnsureCooperativeAsync();
        var owner = await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Password)).Username);
        var role = await factory.RoleAsync(StaffRole.ZoneManager);
        return (owner, await factory.AddZoneAsync(NewCode()), role);
    }

    /// <summary>A valid profile; <paramref name="change"/> overrides single fields.</summary>
    public static Dictionary<string, object?> Profile(string name, string phone, Guid? zoneId = null, Action<Dictionary<string, object?>>? change = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["fullName"] = name,
            ["phoneNumber"] = phone,
            ["birthday"] = "1990-05-01",
            ["workingZoneIds"] = zoneId is { } id ? new[] { id } : Array.Empty<Guid>(),
        };
        change?.Invoke(body);
        return body;
    }

    public static object Bank(string number = "1234567890") =>
        new { bankName = "Vietcombank", accountNumber = number, accountHolderName = "Nguyen Van A" };

    public static object AccountBlock(string username, FarmingZone? zone, Role role, string? phoneNumber = null) =>
        AccountBlock(username, zone, role.Id, phoneNumber);

    public static object AccountBlock(string username, FarmingZone? zone, Guid roleId, string? phoneNumber = null) =>
        new { username, phoneNumber, grants = new[] { new { zoneId = zone?.Id, roleId } } };

    /// <summary>Creates the employee and returns the saved body (fails the test when it is not 201).</summary>
    public static async Task<JsonElement> CreateAsync(HttpClient owner, Dictionary<string, object?> body)
    {
        var response = await owner.PostAsJsonAsync("/api/employees", body);
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static Guid IdOf(JsonElement saved) => saved.GetProperty("employee").GetProperty("id").GetGuid();

    public static string RowVersionOf(JsonElement saved) => saved.GetProperty("employee").GetProperty("rowVersion").GetString()!;

    /// <summary>The response body; buffered, so it can be read again.</summary>
    public static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
    {
        await response.Content.LoadIntoBufferAsync();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    /// <summary>The field a MSG01 problem names.</summary>
    public static async Task<string?> FieldAsync(HttpResponseMessage response)
    {
        var body = await ProblemAsync(response);
        return body.TryGetProperty("field", out var field) ? field.GetString() : $"(no field, HTTP {(int)response.StatusCode}: {body})";
    }
}
