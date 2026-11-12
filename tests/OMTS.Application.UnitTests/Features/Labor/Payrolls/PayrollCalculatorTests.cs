using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Labor.Payrolls;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Labor.Payrolls;

/// <summary>BR-LABOR-10, BR-LABOR-12: the pay is the rounded hours of each job type times the hourly rate in force on each day.</summary>
public sealed class PayrollCalculatorTests
{
    private static readonly DateOnly Start = new(2026, 9, 1);
    private static readonly DateOnly End = new(2026, 9, 30);
    private static readonly Guid RaftId = Guid.NewGuid();

    private static JobType Job(string name, params (decimal Rate, DateOnly From)[] rates)
    {
        var job = new JobType { Name = name };
        foreach (var (rate, from) in rates)
        {
            job.Rates.Add(new PayRate { JobTypeId = job.Id, HourlyRate = rate, EffectiveFrom = from });
        }

        return job;
    }

    private static DateTimeOffset At(DateOnly day, string time) => new(day.ToDateTime(TimeOnly.Parse(time)), TimeSpan.FromHours(7));

    private static Attendance Attended(Employee e, DateOnly day, string? checkIn, string? checkOut) => new()
    {
        EmployeeId = e.Id, Date = day, CheckInAt = checkIn is null ? null : At(day, checkIn), CheckOutAt = checkOut is null ? null : At(day, checkOut),
    };

    private static WorkAssignment Assigned(Employee e, DateOnly day, JobType job, string? startsAt = null) => new()
    {
        EmployeeId = e.Id, Date = day, JobTypeId = job.Id, RaftId = RaftId, StartsAt = startsAt is null ? null : At(day, startsAt),
    };

    private static PayrollWorkData Data(Employee e, IEnumerable<Attendance> attendances, IEnumerable<WorkAssignment> assignments, JobType[] jobs, params AttendanceSettingsVersion[] settings) =>
        new([e], attendances.ToList(), assignments.ToList(), settings, jobs.ToDictionary(j => j.Id));

    private static SlipFigures Calculate(Employee e, PayrollWorkData data) => PayrollCalculator.Calculate(e, Start, End, data);

    [Fact]
    public void A_day_pays_its_hours_at_the_rate_in_force_that_day()
    {
        var worker = new Employee { FullName = "Phúc" };
        var harvest = Job("Thu hoạch", (30_000, new DateOnly(2026, 1, 1)), (32_000, new DateOnly(2026, 9, 11)));
        var days = new[] { new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 11) };
        var data = Data(worker, days.Select(d => Attended(worker, d, "07:00", "15:00")), days.Select(d => Assigned(worker, d, harvest)), [harvest]);

        var figures = Calculate(worker, data);

        Assert.Equal([(30_000m, 480), (32_000m, 480)], figures.Jobs.Select(j => (j.HourlyRate, j.Minutes)));
        Assert.Equal([240_000m, 256_000m], figures.Jobs.Select(j => j.Amount));
        Assert.Equal(496_000m, figures.MainPay);
        Assert.Equal(16m, figures.Hours);
        Assert.Equal("30.000 / 32.000", figures.RateNote);
    }

    [Fact]
    public void The_break_and_the_rounding_of_the_settings_in_force_that_day_apply()
    {
        var worker = new Employee { FullName = "Thắng" };
        var harvest = Job("Thu hoạch", (40_000, new DateOnly(2026, 1, 1)));
        var day = new DateOnly(2026, 9, 5);
        var settings = new AttendanceSettingsVersion
        {
            EffectiveFrom = new DateOnly(2026, 1, 1), BreakStart = new TimeOnly(11, 30), BreakEnd = new TimeOnly(12, 30), RoundingUnit = AttendanceRoundingUnit.Minutes15,
        };
        // 07:00 to 16:10 is 9 h 10 less the 1 h break: 8 h 10, rounded down to 8 h 0.
        var data = Data(worker, [Attended(worker, day, "07:00", "16:10")], [Assigned(worker, day, harvest)], [harvest], settings);

        var figures = Calculate(worker, data);

        Assert.Equal(8m, figures.Hours);
        Assert.Equal(320_000m, figures.MainPay);
    }

    [Fact]
    public void Each_job_type_of_a_day_is_paid_at_its_own_rate()
    {
        var worker = new Employee { FullName = "Nam" };
        var harvest = Job("Thu hoạch", (40_000, new DateOnly(2026, 1, 1)));
        var hanging = Job("Treo giống", (35_000, new DateOnly(2026, 1, 1)));
        var day = new DateOnly(2026, 9, 7);
        var data = Data(worker, [Attended(worker, day, "07:00", "11:00")], [Assigned(worker, day, hanging), Assigned(worker, day, harvest, "09:00")], [harvest, hanging]);

        var figures = Calculate(worker, data);

        Assert.Equal(2, figures.Jobs.Count);
        Assert.Equal(2m * 35_000 + 2m * 40_000, figures.MainPay);
        Assert.Equal("35.000 / 40.000", figures.RateNote);
    }

    [Fact]
    public void An_amount_is_rounded_to_the_vnd()
    {
        var worker = new Employee { FullName = "Hà" };
        var job = Job("Vệ sinh", (33_333, new DateOnly(2026, 1, 1)));
        var day = new DateOnly(2026, 9, 8);
        var data = Data(worker, [Attended(worker, day, "07:00", "08:20")], [Assigned(worker, day, job)], [job]);

        var figures = Calculate(worker, data);

        // 80 minutes at 33,333 per hour is 44,444.
        Assert.Equal(44_444m, figures.MainPay);
        Assert.Equal(1.33m, figures.Hours);
    }

    [Fact]
    public void A_day_with_only_a_check_in_counts_no_hours_and_is_reported()
    {
        var worker = new Employee { FullName = "Mai" };
        var job = Job("Thu hoạch", (40_000, new DateOnly(2026, 1, 1)));
        var (full, half) = (new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 2));
        var data = Data(worker, [Attended(worker, full, "07:00", "11:00"), Attended(worker, half, "07:00", null)], [Assigned(worker, full, job), Assigned(worker, half, job)], [job]);

        var figures = Calculate(worker, data);

        Assert.Equal(4m, figures.Hours);
        Assert.Equal([half], figures.IncompleteDays);
    }

    [Fact]
    public void Hours_before_the_first_rate_are_reported_and_not_paid()
    {
        var worker = new Employee { FullName = "Lan" };
        var job = Job("Thu hoạch", (40_000, new DateOnly(2026, 9, 15)));
        var (early, late) = (new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 15));
        var data = Data(worker, [Attended(worker, early, "07:00", "11:00"), Attended(worker, late, "07:00", "11:00")], [Assigned(worker, early, job), Assigned(worker, late, job)], [job]);

        var figures = Calculate(worker, data);

        Assert.Equal(160_000m, figures.MainPay);
        Assert.Equal([(early, "Thu hoạch")], figures.UnratedDays);
    }

    [Fact]
    public void The_days_of_a_staff_employee_count_nothing_and_a_type_change_splits_the_period()
    {
        var worker = new Employee { FullName = "Sơn" };
        worker.TypePeriods.Add(new EmployeeTypePeriod { EmployeeId = worker.Id, Type = EmployeeType.SeasonalWorker, EffectiveFrom = new DateOnly(2026, 1, 1) });
        worker.TypePeriods.Add(new EmployeeTypePeriod { EmployeeId = worker.Id, Type = EmployeeType.Staff, EffectiveFrom = new DateOnly(2026, 9, 10) });
        var job = Job("Thu hoạch", (40_000, new DateOnly(2026, 1, 1)));
        var days = new[] { new DateOnly(2026, 9, 9), new DateOnly(2026, 9, 10) };
        var data = Data(worker, days.Select(d => Attended(worker, d, "07:00", "11:00")), days.Select(d => Assigned(worker, d, job)), [job]);

        var figures = Calculate(worker, data);

        Assert.Equal(4m, figures.Hours);
    }

    [Fact]
    public void Days_outside_the_period_and_other_employees_are_ignored()
    {
        var worker = new Employee { FullName = "Tuấn" };
        var other = new Employee { FullName = "Khác" };
        var job = Job("Thu hoạch", (40_000, new DateOnly(2026, 1, 1)));
        var (inside, outside) = (new DateOnly(2026, 9, 30), new DateOnly(2026, 10, 1));
        var data = new PayrollWorkData(
            [worker, other],
            [Attended(worker, inside, "07:00", "11:00"), Attended(worker, outside, "07:00", "11:00"), Attended(other, inside, "07:00", "17:00")],
            [Assigned(worker, inside, job), Assigned(worker, outside, job), Assigned(other, inside, job)],
            [], new Dictionary<Guid, JobType> { [job.Id] = job });

        var figures = Calculate(worker, data);

        Assert.Equal(160_000m, figures.MainPay);
    }

    [Fact]
    public void An_employee_without_attendance_has_no_hours_and_no_rate_note()
    {
        var worker = new Employee { FullName = "Trống" };

        var figures = Calculate(worker, new PayrollWorkData([worker], [], [], [], new Dictionary<Guid, JobType>()));

        Assert.Equal((0, 0m), (figures.Minutes, figures.MainPay));
        Assert.Null(figures.RateNote);
    }
}
