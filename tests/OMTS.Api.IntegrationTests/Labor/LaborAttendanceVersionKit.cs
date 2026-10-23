using System.Net;
using System.Text.Json;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>Reads the rowVersion a client must send back to change a saved attendance record (as the sheet shows it).</summary>
public static class LaborAttendanceVersionKit
{
    /// <summary>The rowVersion of the saved record of the person (employee or worker id) on the day.</summary>
    public static async Task<string> AttendanceVersionAsync(this LaborWorld w, DateOnly date, Guid personId, HttpClient? client = null)
    {
        var sheet = await LaborTestKit.ExpectAsync(await (client ?? w.Owner).GetAsync($"{w.Attendance}?date={date:yyyy-MM-dd}"), HttpStatusCode.OK);
        var row = sheet.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("personId").GetGuid() == personId && !r.GetProperty("suggested").GetBoolean());
        return row.GetProperty("rowVersion").GetString()!;
    }
}
