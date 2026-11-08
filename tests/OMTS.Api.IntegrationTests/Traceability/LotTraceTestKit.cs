using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using OMTS.Api.IntegrationTests.Farming;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>TRACE-02 calls and the parts of a trace the tests read.</summary>
public static class LotTraceTestKit
{
    public const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static Task<HttpResponseMessage> GetTraceAsync(this HttpClient client, Guid lotId) => client.GetAsync($"/api/harvest-lots/{lotId}/trace");

    public static async Task<JsonElement> TraceAsync(this HttpClient client, Guid lotId) => await (await client.GetTraceAsync(lotId)).ReadAsync(HttpStatusCode.OK);

    public static Task<HttpResponseMessage> GetTraceExportAsync(this HttpClient client, Guid lotId) => client.GetAsync($"/api/harvest-lots/{lotId}/trace/export");

    public static Task<HttpResponseMessage> SearchTraceAsync(this HttpClient client, string? lotCode = null, string? invoiceCode = null)
    {
        var query = string.Join('&', new[] { ("lotCode", lotCode), ("invoiceCode", invoiceCode) }
            .Where(p => p.Item2 is not null).Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2!)}"));
        return client.GetAsync($"/api/harvest-lots/trace-search?{query}");
    }

    /// <summary>The ids of the lots a search found, in the order of the answer.</summary>
    public static async Task<List<Guid>> FoundAsync(this HttpClient client, string? lotCode = null, string? invoiceCode = null) =>
        (await (await client.SearchTraceAsync(lotCode, invoiceCode)).ReadAsync(HttpStatusCode.OK)).GetProperty("lots").EnumerateArray().Select(l => l.Id()).ToList();

    /// <summary>The invoice codes of the forward trace, in order.</summary>
    public static List<string> InvoiceCodes(this JsonElement trace) => trace.GetProperty("invoices").EnumerateArray().Select(i => i.Text("invoiceCode")).ToList();

    /// <summary>Each lane with the seed lot codes of its sessions, e.g. "2: PN000001-1, PN000002-1".</summary>
    public static List<string> LaneSeedLots(this JsonElement trace) => trace.GetProperty("lanes").EnumerateArray()
        .Select(l => $"{l.GetProperty("laneNumber").GetInt32()}: {string.Join(", ", l.GetProperty("seedings").EnumerateArray().Select(s => s.GetProperty("seedLot").Text("code")))}")
        .ToList();

    /// <summary>The sessions of the trace, whatever the lane, by id.</summary>
    public static Dictionary<Guid, JsonElement> Seedings(this JsonElement trace) => trace.GetProperty("lanes").EnumerateArray()
        .SelectMany(l => l.GetProperty("seedings").EnumerateArray())
        .GroupBy(s => s.GetProperty("seedingRecordId").GetGuid())
        .ToDictionary(g => g.Key, g => g.First());

    /// <summary>The rows of a sheet of the exported file, header row included, as text.</summary>
    public static async Task<Dictionary<string, List<List<string>>>> SheetsAsync(this HttpResponseMessage export)
    {
        using var book = new XLWorkbook(await export.Content.ReadAsStreamAsync());
        return book.Worksheets.ToDictionary(
            s => s.Name,
            s => s.RowsUsed().Select(r => Enumerable.Range(1, s.LastColumnUsed()!.ColumnNumber()).Select(c => r.Cell(c).GetFormattedString()).ToList()).ToList());
    }
}
