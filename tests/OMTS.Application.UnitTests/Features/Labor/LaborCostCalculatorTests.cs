using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Labor.Costs;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Labor;

/// <summary>BR-LABOR-07: the pay of a day is shared between the rafts of the job periods, and the shares always add up to the pay.</summary>
public sealed class LaborCostCalculatorTests
{
    private static readonly DateOnly Day = new(2026, 3, 2);
    private static readonly Guid Zone = Guid.NewGuid();
    private static readonly Guid RaftA = Guid.NewGuid();
    private static readonly Guid RaftB = Guid.NewGuid();

    private static DateTimeOffset At(string time) => new(Day.ToDateTime(TimeOnly.Parse(time)), TimeSpan.FromHours(7));

    private static (JobType Job, Employee Worker) Setup(decimal hourlyRate, EmployeeType type = EmployeeType.SeasonalWorker)
    {
        var job = new JobType { Name = "Harvest" };
        job.Rates.Add(new PayRate { JobTypeId = job.Id, HourlyRate = hourlyRate, EffectiveFrom = new DateOnly(2020, 1, 1) });
        var worker = new Employee();
        worker.TypePeriods.Add(new EmployeeTypePeriod { EmployeeId = worker.Id, Type = type, EffectiveFrom = new DateOnly(2020, 1, 1) });
        return (job, worker);
    }

    private static WorkAssignment Period(Employee worker, JobType job, Guid? raft, string? startsAt) => new()
    {
        EmployeeId = worker.Id, Date = Day, JobTypeId = job.Id, RaftId = raft, ZoneId = Zone, StartsAt = startsAt is null ? null : At(startsAt),
    };

    private static PayrollWorkData Data(Employee worker, JobType job, string checkIn, string? checkOut, params WorkAssignment[] periods) =>
        new([worker], [new Attendance { EmployeeId = worker.Id, Date = Day, CheckInAt = At(checkIn), CheckOutAt = checkOut is null ? null : At(checkOut) }],
            periods, [], new Dictionary<Guid, JobType> { [job.Id] = job });

    [Fact]
    public void A_day_of_170000_on_two_rafts_for_the_same_time_gives_85000_to_each()
    {
        var (job, worker) = Setup(20_000m);
        var data = Data(worker, job, "06:00", "14:30", Period(worker, job, RaftA, null), Period(worker, job, RaftB, "10:15"));

        var costs = LaborCostCalculator.Calculate(Day, Day, data);

        Assert.Equal(170_000m, costs.Sum(c => c.Amount));
        Assert.Equal([85_000m, 85_000m], costs.OrderBy(c => c.RaftId == RaftA ? 0 : 1).Select(c => c.Amount));
    }

    [Fact]
    public void The_remainder_of_the_rounding_goes_to_the_last_period_so_the_shares_add_up()
    {
        // 1 hour at 10,000 VND split 1 : 1 : 1 by minutes would give 3,333.33 each.
        var (job, worker) = Setup(10_000m);
        var third = Guid.NewGuid();
        var data = Data(worker, job, "06:00", "07:00", Period(worker, job, RaftA, null), Period(worker, job, RaftB, "06:20"), Period(worker, job, third, "06:40"));

        var costs = LaborCostCalculator.Calculate(Day, Day, data);

        Assert.Equal(10_000m, costs.Sum(c => c.Amount));
        Assert.Equal(3_334m, costs.Single(c => c.RaftId == third).Amount);
    }

    [Fact]
    public void Work_without_a_raft_is_the_cost_of_its_zone()
    {
        var (job, worker) = Setup(30_000m);
        var data = Data(worker, job, "06:00", "08:00", Period(worker, job, null, null));

        var cost = Assert.Single(LaborCostCalculator.Calculate(Day, Day, data));

        Assert.Null(cost.RaftId);
        Assert.Equal(Zone, cost.ZoneId);
        Assert.Equal(60_000m, cost.Amount);
    }

    [Fact]
    public void Staff_days_and_incomplete_days_cost_nothing()
    {
        var (job, staff) = Setup(30_000m, EmployeeType.Staff);
        Assert.Empty(LaborCostCalculator.Calculate(Day, Day, Data(staff, job, "06:00", "14:00", Period(staff, job, RaftA, null))));

        var (job2, seasonal) = Setup(30_000m);
        Assert.Empty(LaborCostCalculator.Calculate(Day, Day, Data(seasonal, job2, "06:00", null, Period(seasonal, job2, RaftA, null))));
    }

    [Fact]
    public void Hours_with_no_rate_in_force_cost_nothing()
    {
        var (job, worker) = Setup(30_000m);
        job.Rates.Single().EffectiveFrom = Day.AddDays(1);

        Assert.Empty(LaborCostCalculator.Calculate(Day, Day, Data(worker, job, "06:00", "14:00", Period(worker, job, RaftA, null))));
    }
}
