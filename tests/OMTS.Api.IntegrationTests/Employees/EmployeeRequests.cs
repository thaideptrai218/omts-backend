using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>Request builders for the LABOR-01 tests; names and phones are unique so tests can share a database.</summary>
internal static class EmployeeRequests
{
    public static async Task<(HttpClient Owner, FarmingZone Zone, Role Role)> SetupAsync(AuthApiFactory factory)
    {
        await factory.EnsureCooperativeAsync();
        var owner = await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Password)).Username);
        var role = await factory.RoleAsync(StaffRole.ZoneManager);
        return (owner, await factory.AddZoneAsync(NewCode()), role);
    }

    /// <summary>A logged-in Zone Manager of the zone, or of All zones when null (real rights of the §I.4.4 matrix).</summary>
    public static async Task<HttpClient> ManagerAsync(AuthApiFactory factory, FarmingZone? zone, StaffRole role = StaffRole.ZoneManager)
    {
        var account = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await factory.GrantAsync(account, role, zone);
        return await factory.LoginAsync(account.Username);
    }

    /// <summary>A valid profile of a seasonal worker; <paramref name="change"/> overrides single fields.</summary>
    public static Dictionary<string, object?> Profile(string name, string? phone, Guid? zoneId = null, Action<Dictionary<string, object?>>? change = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["fullName"] = name,
            ["phoneNumber"] = phone,
            ["employeeType"] = nameof(EmployeeType.SeasonalWorker),
            ["birthday"] = "1990-05-01",
            ["workingZoneIds"] = zoneId is { } id ? new[] { id } : Array.Empty<Guid>(),
        };
        change?.Invoke(body);
        return body;
    }

    /// <summary>What a Zone Manager sends: the profile with the confirmation of LABOR-01 step 5.</summary>
    public static Dictionary<string, object?> ApprovedProfile(string name, string? phone, Guid zoneId) =>
        Profile(name, phone, zoneId, b => b["ownerApproved"] = true);

    public static object Bank(string number = "999900000001") =>
        new { bankName = "Vietcombank", accountNumber = number, accountHolderName = "Nguyen Van A" };

    public static object AccountBlock(string username, FarmingZone? zone, Role role) => AccountBlock(username, zone, role.Id);

    public static object AccountBlock(string username, FarmingZone? zone, Guid roleId) =>
        new { username, grants = new[] { new { zoneId = zone?.Id, roleId } } };

    /// <summary>Creates the employee and returns the saved body (fails the test when it is not 201).</summary>
    public static async Task<JsonElement> CreateAsync(HttpClient client, Dictionary<string, object?> body)
    {
        var response = await client.PostAsJsonAsync("/api/employees", body);
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static async Task<JobType> AddJobTypeAsync(AuthApiFactory factory, Action<JobType>? change = null)
    {
        var jobType = new JobType { Name = $"Job {NewCode()}" };
        change?.Invoke(jobType);
        await factory.UseDbAsync(db => db.JobTypes.Add(jobType));
        return jobType;
    }

    public static Guid IdOf(JsonElement saved) => saved.GetProperty("employee").GetProperty("id").GetGuid();

    public static string RowVersionOf(JsonElement saved) => saved.GetProperty("employee").GetProperty("rowVersion").GetString()!;

    /// <summary>Today in the business time zone, read when the test runs.</summary>
    public static DateOnly Today() => OMTS.Application.Common.BusinessClock.ToBusinessDate(DateTimeOffset.UtcNow);

    /// <summary>The body with the version the employee has now.</summary>
    public static async Task<object> EndBodyAsync(HttpClient client, Guid id, DateOnly lastWorkingDay) =>
        new { lastWorkingDay = lastWorkingDay.ToString("yyyy-MM-dd"), rowVersion = await CurrentVersionAsync(client, id) };

    public static async Task<string> CurrentVersionAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/employees/{id}")).GetProperty("rowVersion").GetString()!;

    /// <summary>Ends the employment on an earlier day, so the employee is Left at once.</summary>
    public static async Task<JsonElement> EndAsync(HttpClient client, Guid id, DateOnly? lastWorkingDay = null)
    {
        var response = await client.PostAsJsonAsync($"/api/employees/{id}/end-employment", await EndBodyAsync(client, id, lastWorkingDay ?? Today().AddDays(-1)));
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

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
