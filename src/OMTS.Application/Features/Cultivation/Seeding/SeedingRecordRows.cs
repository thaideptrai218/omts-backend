using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.Cultivation.Seeding;

/// <summary>
/// The rows of a corrected session, in CYCLE-02.2 and in TRACE-03: the date, seed lot and quantity change in place; a lane the correction takes out goes, a lane it adds
/// comes, and a lane it keeps stays as it is.
/// </summary>
internal static class SeedingRecordRows
{
    public static void Apply(ISeedingRecordRepository records, SeedingRecord record, SeedingFields fields)
    {
        (record.SeedingDate, record.Quantity) = (fields.Date, fields.Quantity);
        (record.SeedLotLineId, record.SeedLotLine) = (fields.Lot.Id, fields.Lot);

        var wanted = fields.Lanes.ToDictionary(l => l.Id);
        foreach (var lane in record.Lanes.Where(l => !wanted.ContainsKey(l.LaneId)).ToList())
        {
            record.Lanes.Remove(lane);
            records.RemoveLane(lane);
        }

        foreach (var lane in fields.Lanes.Where(l => record.Lanes.All(existing => existing.LaneId != l.Id)))
        {
            var added = new SeedingRecordLane { SeedingRecordId = record.Id, LaneId = lane.Id, Lane = lane };
            record.Lanes.Add(added);
            records.AddLane(added);
        }
    }
}
