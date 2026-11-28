using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Labor.Costs;
using OMTS.Application.Features.Labor.Payrolls;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Labor;

/// <summary>BR-LABOR-07: the raft cost is the payroll pay of the work (by hour, by shift, or the field shifts of a fixed salary), shared between the rafts of the job periods; the shares always add up to the pay.</summary>
public sealed class LaborCostCalculatorTests
{
    private static readonly DateOnly Day = new(2026, 3, 2);
    private static readonly Guid Zone = Guid.NewGuid();
    private static readonly Guid RaftA = Guid.NewGuid();
    private static readonly Guid RaftB = Guid.NewGuid();

    private static DateTimeOffset At(string time) => new(Day.ToDateTime(TimeOnly.Parse(time)), TimeSpan.FromHours(7));

    private static (JobType Job, Employee Worker) Setup(
        decimal hourlyRate, EmployeeType type = EmployeeType.SeasonalWorker, SalaryType? salary = SalaryType.Hourly, bool fieldShifts = false, decimal shiftRate = 0)
    {
        var job = new JobType { Name = "Harvest" };
        job.Rates.Add(new PayRate { JobTypeId = job.Id, HourlyRate = hourlyRate, EffectiveFrom = new DateOnly(2020, 1, 1) });
        if (shiftRate > 0)
        {
            job.ShiftRates.Add(new ShiftRate { JobTypeId = job.Id, Amount = shiftRate, EffectiveFrom = new DateOnly(2020, 1, 1) });
        }

        var worker = new Employee();
        worker.TypePeriods.Add(new EmployeeTypePeriod { EmployeeId = worker.Id, Type = type, EffectiveFrom = new DateOnly(2020, 1, 1) });
        if (salary is { } salaryType)
        {
            worker.SalarySetups.Add(new EmployeeSalarySetup
            {
                EmployeeId = worker.Id, Type = salaryType, MonthlyAmount = salaryType == SalaryType.FixedMonthly ? 9_000_000 : null, PayFieldShifts = fieldShifts, EffectiveFrom = new DateOnly(2020, 1, 1),
            });
        }

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
    public void Incomplete_days_and_staff_with_no_pay_for_the_work_cost_nothing()
    {
        var (job, staff) = Setup(30_000m, EmployeeType.Staff, salary: null);
        Assert.Empty(LaborCostCalculator.Calculate(Day, Day, Data(staff, job, "06:00", "14:00", Period(staff, job, RaftA, null))));

        var (jobFixed, fixedStaff) = Setup(30_000m, EmployeeType.Staff, SalaryType.FixedMonthly, fieldShifts: false, shiftRate: 300_000m);
        Assert.Empty(LaborCostCalculator.Calculate(Day, Day, Data(fixedStaff, jobFixed, "06:00", "14:00", Period(fixedStaff, jobFixed, RaftA, null))));

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

    [Fact]
    public void A_worker_paid_per_shift_costs_the_shifts_times_the_shift_rate_shared_between_the_rafts()
    {
        // 06:00-14:30 with no break settings is 510 minutes: 2.5 shifts at 300,000 = 750,000, half to each raft.
        var (job, worker) = Setup(20_000m, salary: SalaryType.PerShift, shiftRate: 300_000m);
        var data = Data(worker, job, "06:00", "14:30", Period(worker, job, RaftA, null), Period(worker, job, RaftB, "10:15"));

        var pieces = LaborCostCalculator.CalculatePieces(Day, Day, data);

        Assert.Equal(750_000m, pieces.Sum(p => p.Amount));
        Assert.All(pieces, p => Assert.Equal(375_000m, p.Amount));
        Assert.All(pieces, p => Assert.Equal(PayLineKind.Shift, p.Kind));
        Assert.Equal(2.5m, pieces.Sum(p => p.Shifts));
        Assert.All(pieces, p => Assert.Equal(EmployeeType.SeasonalWorker, p.EmployeeType));
    }

    [Fact]
    public void A_seasonal_worker_with_no_setup_is_paid_per_shift()
    {
        var (job, worker) = Setup(20_000m, salary: null, shiftRate: 300_000m);

        var cost = Assert.Single(LaborCostCalculator.Calculate(Day, Day, Data(worker, job, "06:00", "14:30", Period(worker, job, RaftA, null))));

        Assert.Equal(750_000m, cost.Amount);
    }

    [Fact]
    public void A_fixed_salary_employee_with_field_shifts_costs_only_the_shifts_of_the_days_with_an_assignment()
    {
        var (job, staff) = Setup(20_000m, EmployeeType.Staff, SalaryType.FixedMonthly, fieldShifts: true, shiftRate: 300_000m);

        var pieces = LaborCostCalculator.CalculatePieces(Day, Day, Data(staff, job, "06:00", "14:30", Period(staff, job, RaftA, null)));

        var piece = Assert.Single(pieces);
        Assert.Equal(750_000m, piece.Amount);
        Assert.Equal(PayLineKind.FieldShift, piece.Kind);
        Assert.Equal(EmployeeType.Staff, piece.EmployeeType);
        Assert.Equal(RaftA, piece.RaftId);
    }

    [Fact]
    public void An_hourly_staff_employee_costs_the_hours_times_the_rate()
    {
        var (job, staff) = Setup(20_000m, EmployeeType.Staff, SalaryType.Hourly);

        var cost = Assert.Single(LaborCostCalculator.Calculate(Day, Day, Data(staff, job, "06:00", "14:30", Period(staff, job, RaftA, null))));

        Assert.Equal(170_000m, cost.Amount);
    }

    [Fact]
    public void The_cost_of_a_raft_equals_the_pay_of_the_payslip_for_the_same_work()
    {
        var (job, worker) = Setup(20_000m, salary: SalaryType.PerShift, shiftRate: 333_333m);
        var data = Data(worker, job, "06:00", "14:30", Period(worker, job, RaftA, null), Period(worker, job, RaftB, "09:00"));

        var slip = PayrollCalculator.Calculate(worker, Day, Day, PayrollPeriodType.Custom, data);

        Assert.Equal(slip.MainPay, LaborCostCalculator.Calculate(Day, Day, data).Sum(c => c.Amount));
    }
}
