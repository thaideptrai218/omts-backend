using OMTS.Application.Features.Workforce.Dtos;
using OMTS.Domain.Entities.Labor;
namespace OMTS.Application.Features.Workforce;

/// <summary>BR-LABOR-05: deduct overlap with the break, sum by job across rafts, then round DOWN once.</summary>
public static class AttendanceCalculator
{
    public static AttendanceDayView View(AttendanceRecord row, IReadOnlyList<AttendanceSettings> settings)
    {
        var complete = row.CheckedInAt is { } start && row.CheckedOutAt is { } end && end > start;
        var periods = new List<AttendancePeriodView>();
        var setting = settings.Where(s => s.EffectiveFrom <= row.WorkDate).OrderByDescending(s => s.EffectiveFrom).FirstOrDefault();
        if (complete)
        {
            var entries = row.JobPeriods.OrderBy(p => p.StartsAt).ToList();
            for (var i = 0; i < entries.Count; i++)
            {
                var p = entries[i];
                var from = p.StartsAt < row.CheckedInAt!.Value ? row.CheckedInAt.Value : p.StartsAt;
                var to = i + 1 < entries.Count ? entries[i + 1].StartsAt : row.CheckedOutAt!.Value;
                if (to > row.CheckedOutAt!.Value) to = row.CheckedOutAt.Value;
                var minutes = Math.Max(0, (decimal)(to - from).TotalMinutes);
                if (setting?.BreakFrom is { } breakFrom && setting.BreakTo is { } breakTo)
                {
                    var b1 = new DateTimeOffset(row.WorkDate.ToDateTime(breakFrom), TimeSpan.FromHours(7));
                    var b2 = new DateTimeOffset(row.WorkDate.ToDateTime(breakTo), TimeSpan.FromHours(7));
                    var overlap = (b2 < to ? b2 : to) - (b1 > from ? b1 : from);
                    minutes -= Math.Max(0, (decimal)overlap.TotalMinutes);
                }
                periods.Add(new(p.JobTypeId, p.JobType.Name, p.Raft.Code, from, to, Math.Max(0, minutes)));
            }
        }
        var unit = setting?.RoundingMinutes ?? 0;
        var jobs = periods.GroupBy(p => new { p.JobTypeId, p.Job }).Select(g =>
        {
            var minutes = g.Sum(p => p.Minutes);
            return new JobHoursView(g.Key.JobTypeId, g.Key.Job, (unit == 0 ? minutes : Math.Floor(minutes / unit) * unit) / 60);
        }).ToList();
        return new(row.WorkDate, row.CheckedInAt, row.CheckedOutAt, periods, jobs, complete);
    }
}
