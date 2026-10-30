using OMTS.Application.Features.Workforce;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Labor;
namespace OMTS.Application.UnitTests.Workforce;

public sealed class AttendanceCalculatorTests
{
    [Fact]
    public void Srs_example_subtracts_break_and_rounds_down_per_job()
    {
        var row = Row("06:10", "16:55");
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        row.JobPeriods.Add(Period(first, "06:10")); row.JobPeriods.Add(Period(second, "09:40"));
        var view = AttendanceCalculator.View(row, [new AttendanceSettings { EffectiveFrom = row.WorkDate, BreakFrom = new(11, 30), BreakTo = new(12, 30), RoundingMinutes = 15 }]);
        Assert.True(view.Complete);
        Assert.Equal(3.5m, view.Jobs.Single(j => j.JobTypeId == first).Hours);
        Assert.Equal(6.25m, view.Jobs.Single(j => j.JobTypeId == second).Hours);
    }
    [Fact]
    public void Same_job_across_rafts_is_rounded_once_after_summing()
    {
        var row = Row("06:00", "10:20"); var job = Guid.NewGuid();
        row.JobPeriods.Add(Period(job, "06:00")); row.JobPeriods.Add(Period(job, "08:10"));
        var view = AttendanceCalculator.View(row, [new AttendanceSettings { EffectiveFrom = row.WorkDate, RoundingMinutes = 15 }]);
        Assert.Equal(4.25m, Assert.Single(view.Jobs).Hours);
    }
    [Fact]
    public void Missing_check_in_keeps_no_hours()
    {
        var row = Row("06:00", "10:20"); row.CheckedInAt = null;
        Assert.Empty(AttendanceCalculator.View(row, []).Jobs);
    }
    [Fact]
    public void Future_setting_does_not_change_earlier_days_and_default_has_no_rounding()
    {
        var row = Row("06:00", "10:20"); row.JobPeriods.Add(Period(Guid.NewGuid(), "06:00"));
        var view = AttendanceCalculator.View(row, [new AttendanceSettings { EffectiveFrom = row.WorkDate.AddDays(1), RoundingMinutes = 60 }]);
        Assert.Equal(260m / 60, Assert.Single(view.Jobs).Hours);
    }
    private static AttendanceRecord Row(string start, string end) => new()
    {
        WorkDate = new(2026, 10, 30), CheckedInAt = At(start), CheckedOutAt = At(end)
    };
    private static DateTimeOffset At(string time) => DateTimeOffset.Parse("2026-10-30T" + time + ":00+07:00");
    private static AttendanceJobPeriod Period(Guid job, string from) => new()
    {
        JobTypeId = job, JobType = new JobType { Name = "Việc" }, Raft = new Raft { Code = "B01" }, StartsAt = At(from)
    };
}
