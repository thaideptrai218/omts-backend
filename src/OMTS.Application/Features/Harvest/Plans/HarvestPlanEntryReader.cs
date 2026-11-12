using OMTS.Application.Features.Purchasing;

namespace OMTS.Application.Features.Harvest.Plans;

/// <summary>One line as read: the raft, the lanes to harvest by number, the cages expected, and the line of the stored plan it stands for (none: a new line).</summary>
internal sealed record HarvestPlanLineEntries(int LineNumber, Guid? Id, Guid RaftId, IReadOnlyList<int> LaneNumbers, int? ExpectedCageCount, string? Note);

/// <summary>The fields of a plan once their shapes are right; what they name is checked against the database after.</summary>
internal sealed record HarvestPlanEntries(
    string? Code, DateTimeOffset PlannedAt, string? Note, IReadOnlyList<Guid> SalesOrderIds, IReadOnlyList<HarvestPlanLineEntries> Lines, string? RowVersion);

/// <summary>The shape of each entry of a plan (MSG-GEN-01 names every field to fix): at least one line, each with a raft and at least one lane given once, cages above 0.</summary>
internal static class HarvestPlanEntryReader
{
    public static (HarvestPlanEntries? Entries, List<string> Invalid) Read(SaveHarvestPlanRequest request)
    {
        var invalid = new List<string>();
        if (!DocumentCodes.TypedAllowed(request.Code))
        {
            invalid.Add("Code");
        }

        if (request.PlannedAt is null)
        {
            invalid.Add("Planned at");
        }

        var note = request.Note?.Trim();
        if (note?.Length > HarvestPlanRules.MaxNoteLength)
        {
            invalid.Add("Note");
        }

        var orderIds = (request.SalesOrderIds ?? []).Distinct().ToList();
        if (orderIds.Count > HarvestPlanRules.MaxOrders || orderIds.Contains(Guid.Empty))
        {
            invalid.Add("Sales orders");
        }

        var sent = request.Lines ?? [];
        if (sent.Count is 0 or > HarvestPlanRules.MaxLines)
        {
            invalid.Add("Lines");
        }

        var lines = new List<HarvestPlanLineEntries>();
        var number = 0;
        foreach (var line in sent)
        {
            number++;
            var field = $"Line {number}";
            var lanes = line.LaneNumbers ?? [];
            if (line.RaftId is null || line.RaftId == Guid.Empty)
            {
                invalid.Add($"{field} raft");
            }

            if (lanes.Count == 0 || lanes.Any(n => n <= 0) || lanes.Distinct().Count() != lanes.Count)
            {
                invalid.Add($"{field} lanes");
            }

            if (line.ExpectedCageCount is <= 0)
            {
                invalid.Add($"{field} cages");
            }

            var lineNote = line.Note?.Trim();
            if (lineNote?.Length > HarvestPlanRules.MaxLineNoteLength)
            {
                invalid.Add($"{field} note");
            }

            lines.Add(new HarvestPlanLineEntries(
                number, line.Id, line.RaftId ?? Guid.Empty, lanes.Order().ToList(), line.ExpectedCageCount, string.IsNullOrEmpty(lineNote) ? null : lineNote));
        }

        var ids = lines.Where(l => l.Id is not null).Select(l => l.Id!.Value).ToList();
        if (ids.Distinct().Count() != ids.Count)
        {
            invalid.Add("Lines");
        }

        if (invalid.Count > 0)
        {
            return (null, invalid.Distinct().ToList());
        }

        return (new HarvestPlanEntries(
            DocumentCodes.Typed(request.Code), request.PlannedAt!.Value, string.IsNullOrEmpty(note) ? null : note, orderIds, lines, request.RowVersion), invalid);
    }
}
