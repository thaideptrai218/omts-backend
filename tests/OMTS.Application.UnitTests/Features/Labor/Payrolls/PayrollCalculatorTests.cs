using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Labor.Payrolls;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Labor.Payrolls;

/// <summary>
/// BR-LABOR-10, BR-LABOR-12: the pay of each day follows the salary setup in force that day: hours times the hourly rate, shifts times the shift rate, or a fixed monthly
/// amount with the shifts of field work on top.
/// </summary>
public sealed class PayrollCalculatorTests
{
    private static readonly DateOnly Start = new(2026, 9, 1);
    private static readonly DateOnly End = new(2026, 9, 30);
    private static readonly DateOnly Long = new(2026, 1, 1);
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

    private static JobType ShiftJob(string name, decimal shiftRate, DateOnly from)
    {
        var job = new JobType { Name = name };
        job.ShiftRates.Add(new ShiftRate { JobTypeId = job.Id, Amount = shiftRate, EffectiveFrom = from });
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

    private static Employee Seasonal(string name, params EmployeeSalarySetup[] setups) => WithSetups(new Employee { FullName = name }, setups);

    private static Employee Staff(string name, params EmployeeSalarySetup[] setups)
    {
        var employee = new Employee { FullName = name };
        employee.TypePeriods.Add(new EmployeeTypePeriod { EmployeeId = employee.Id, Type = EmployeeType.Staff, EffectiveFrom = Long });
        return WithSetups(employee, setups);
    }

    private static Employee WithSetups(Employee employee, EmployeeSalarySetup[] setups)
    {
        foreach (var setup in setups)
        {
            setup.EmployeeId = employee.Id;
            employee.SalarySetups.Add(setup);
        }

        return employee;
    }

    private static EmployeeSalarySetup Hourly(DateOnly from = default) => new() { Type = SalaryType.Hourly, EffectiveFrom = from == default ? Long : from };

    private static EmployeeSalarySetup PerShift(DateOnly from = default) => new() { Type = SalaryType.PerShift, EffectiveFrom = from == default ? Long : from };

    private static EmployeeSalarySetup Fixed(decimal amount, bool fieldShifts, DateOnly from = default) =>
        new() { Type = SalaryType.FixedMonthly, MonthlyAmount = amount, PayFieldShifts = fieldShifts, EffectiveFrom = from == default ? Long : from };

    private static PayrollWorkData Data(Employee e, IEnumerable<Attendance> attendances, IEnumerable<WorkAssignment> assignments, JobType[] jobs, params AttendanceSettingsVersion[] settings) =>
        new([e], attendances.ToList(), assignments.ToList(), settings, jobs.ToDictionary(j => j.Id));

    private static PayrollWorkData Nothing(Employee e) => new([e], [], [], [], new Dictionary<Guid, JobType>());

    private static SlipFigures Calculate(Employee e, PayrollWorkData data, PayrollPeriodType type = PayrollPeriodType.Monthly) =>
        PayrollCalculator.Calculate(e, Start, End, type, data);

    private static AttendanceSettingsVersion Break(AttendanceRoundingUnit rounding = AttendanceRoundingUnit.None) => new()
    {
        EffectiveFrom = Long, BreakStart = new TimeOnly(11, 30), BreakEnd = new TimeOnly(12, 30), RoundingUnit = rounding,
    };

    // Hourly pay

    [Fact]
    public void A_day_pays_its_hours_at_the_rate_in_force_that_day()
    {
        var worker = Seasonal("Phúc", Hourly());
        var harvest = Job("Thu hoạch", (30_000, new DateOnly(2026, 1, 1)), (32_000, new DateOnly(2026, 9, 11)));
        var days = new[] { new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 11) };
        var data = Data(worker, days.Select(d => Attended(worker, d, "07:00", "15:00")), days.Select(d => Assigned(worker, d, harvest)), [harvest]);

        var figures = Calculate(worker, data);

        Assert.Equal([(30_000m, 480), (32_000m, 480)], figures.Jobs.Select(j => (j.HourlyRate, j.Minutes)));
        Assert.Equal([240_000m, 256_000m], figures.Jobs.Select(j => j.Amount));
        Assert.Equal(496_000m, figures.MainPay);
        Assert.Equal(0m, figures.ShiftPay);
        Assert.Equal(16m, figures.Hours);
        Assert.Equal("30.000 / 32.000", figures.RateNote);
    }

    [Fact]
    public void The_break_and_the_rounding_of_the_settings_in_force_that_day_apply()
    {
        var worker = Seasonal("Thắng", Hourly());
        var harvest = Job("Thu hoạch", (40_000, new DateOnly(2026, 1, 1)));
        var day = new DateOnly(2026, 9, 5);
        // 07:00 to 16:10 is 9 h 10 less the 1 h break: 8 h 10, rounded down to 8 h 0.
        var data = Data(worker, [Attended(worker, day, "07:00", "16:10")], [Assigned(worker, day, harvest)], [harvest], Break(AttendanceRoundingUnit.Minutes15));

        var figures = Calculate(worker, data);

        Assert.Equal(8m, figures.Hours);
        Assert.Equal(320_000m, figures.MainPay);
    }

    [Fact]
    public void Each_job_type_of_a_day_is_paid_at_its_own_rate()
    {
        var worker = Seasonal("Nam", Hourly());
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
        var worker = Seasonal("Hà", Hourly());
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
        var worker = Seasonal("Mai", Hourly());
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
        var worker = Seasonal("Lan", Hourly());
        var job = Job("Thu hoạch", (40_000, new DateOnly(2026, 9, 15)));
        var (early, late) = (new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 15));
        var data = Data(worker, [Attended(worker, early, "07:00", "11:00"), Attended(worker, late, "07:00", "11:00")], [Assigned(worker, early, job), Assigned(worker, late, job)], [job]);

        var figures = Calculate(worker, data);

        Assert.Equal(160_000m, figures.MainPay);
        Assert.Equal([new UnratedDay(early, "Thu hoạch", PayLineKind.Hourly)], figures.UnratedDays);
    }

    [Fact]
    public void A_staff_employee_with_an_hourly_setup_is_paid_for_the_hours_like_anyone_else()
    {
        var worker = Staff("Sơn", Hourly());
        var job = Job("Thu hoạch", (40_000, new DateOnly(2026, 1, 1)));
        var day = new DateOnly(2026, 9, 9);
        var data = Data(worker, [Attended(worker, day, "07:00", "11:00")], [Assigned(worker, day, job)], [job]);

        var figures = Calculate(worker, data);

        Assert.Equal(160_000m, figures.MainPay);
        Assert.False(figures.MissingSetup);
    }

    [Fact]
    public void Days_outside_the_period_and_other_employees_are_ignored()
    {
        var worker = Seasonal("Tuấn", Hourly());
        var other = Seasonal("Khác", Hourly());
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
        var worker = Seasonal("Trống", Hourly());

        var figures = Calculate(worker, Nothing(worker));

        Assert.Equal((0, 0m), (figures.Minutes, figures.MainPay));
        Assert.Null(figures.RateNote);
        Assert.False(figures.OnSheet);
    }

    // Shift pay

    [Fact]
    public void A_seasonal_worker_without_a_setup_is_paid_per_shift()
    {
        var worker = Seasonal("Phúc");
        var harvest = ShiftJob("Thu hoạch", 300_000, Long);
        var day = new DateOnly(2026, 9, 10);
        // 07:00 to 18:00 less the 1 h break is 10 h: 3 shifts.
        var data = Data(worker, [Attended(worker, day, "07:00", "18:00")], [Assigned(worker, day, harvest)], [harvest], Break());

        var figures = Calculate(worker, data);

        Assert.Equal(SalaryType.PerShift, figures.SalaryType);
        Assert.Equal(3m, figures.Shifts);
        Assert.Equal(900_000m, figures.MainPay);
        Assert.Equal(0m, figures.ShiftPay);
        Assert.Equal([PayLineKind.Shift], figures.Jobs.Select(j => j.Kind));
        Assert.Equal("300.000 đ/ca", figures.RateNote);
    }

    [Theory]
    [InlineData("07:00", "18:00", 3.0)]
    [InlineData("05:00", "17:00", 3.5)]
    [InlineData("07:00", "15:50", 2.5)]
    [InlineData("07:10", "17:00", 3.0)]
    public void A_day_counts_its_shifts_from_the_worked_time_less_the_break_plus_the_tolerance(string checkIn, string checkOut, double shifts)
    {
        var worker = Seasonal("Ca");
        var harvest = ShiftJob("Thu hoạch", 300_000, Long);
        var day = new DateOnly(2026, 9, 10);
        var data = Data(worker, [Attended(worker, day, checkIn, checkOut)], [Assigned(worker, day, harvest)], [harvest], Break());

        var figures = Calculate(worker, data);

        Assert.Equal((decimal)shifts, figures.Shifts);
        Assert.Equal((decimal)shifts * 300_000m, figures.MainPay);
    }

    [Fact]
    public void The_shift_rate_in_force_on_each_day_applies()
    {
        var worker = Seasonal("Phúc");
        var harvest = new JobType { Name = "Thu hoạch" };
        harvest.ShiftRates.Add(new ShiftRate { JobTypeId = harvest.Id, Amount = 300_000, EffectiveFrom = Long });
        harvest.ShiftRates.Add(new ShiftRate { JobTypeId = harvest.Id, Amount = 320_000, EffectiveFrom = new DateOnly(2026, 9, 11) });
        var days = new[] { new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 11) };
        // 07:00 to 11:00 is 4 h: 255 minutes with the tolerance, 1 shift of 180 (the next half shift starts at 270).
        var data = Data(worker, days.Select(d => Attended(worker, d, "07:00", "11:00")), days.Select(d => Assigned(worker, d, harvest)), [harvest]);

        var figures = Calculate(worker, data);

        Assert.Equal([(300_000m, 1m), (320_000m, 1m)], figures.Jobs.Select(j => (j.ShiftRate, j.Shifts)));
        Assert.Equal(300_000m + 320_000m, figures.MainPay);
    }

    [Fact]
    public void Shifts_of_a_job_type_without_a_shift_rate_are_reported_and_not_paid()
    {
        var worker = Seasonal("Lan");
        var job = Job("Thu hoạch", (40_000, Long));
        var day = new DateOnly(2026, 9, 14);
        var data = Data(worker, [Attended(worker, day, "07:00", "15:00")], [Assigned(worker, day, job)], [job]);

        var figures = Calculate(worker, data);

        Assert.Equal(0m, figures.MainPay);
        Assert.Equal([new UnratedDay(day, "Thu hoạch", PayLineKind.Shift)], figures.UnratedDays);
    }

    [Fact]
    public void Too_little_work_for_the_first_half_shift_pays_nothing_and_needs_no_rate()
    {
        var worker = Seasonal("Ngắn");
        var job = Job("Thu hoạch", (40_000, Long));
        var day = new DateOnly(2026, 9, 14);
        // 50 minutes and the 15 minute tolerance are 65 of the 90 minutes of a half shift.
        var data = Data(worker, [Attended(worker, day, "07:00", "07:50")], [Assigned(worker, day, job)], [job]);

        var figures = Calculate(worker, data);

        Assert.Equal(0m, figures.Shifts);
        Assert.Empty(figures.UnratedDays);
    }

    [Fact]
    public void A_shift_length_and_tolerance_from_the_settings_in_force_apply()
    {
        var worker = Seasonal("Ca");
        var harvest = ShiftJob("Thu hoạch", 100_000, Long);
        var day = new DateOnly(2026, 9, 10);
        var settings = new AttendanceSettingsVersion { EffectiveFrom = Long, ShiftMinutes = 240, ShiftToleranceMinutes = 0 };
        // 8 h of work in shifts of 4 h with no tolerance: 2 shifts.
        var data = Data(worker, [Attended(worker, day, "07:00", "15:00")], [Assigned(worker, day, harvest)], [harvest], settings);

        Assert.Equal(2m, Calculate(worker, data).Shifts);
    }

    [Fact]
    public void Each_job_type_of_a_day_counts_its_own_shifts()
    {
        var worker = Seasonal("Nam");
        var harvest = ShiftJob("Thu hoạch", 300_000, Long);
        var hanging = ShiftJob("Treo giống", 270_000, Long);
        var day = new DateOnly(2026, 9, 7);
        // 07:00 to 13:00 with no break: 3 h of hanging (1 shift) and 3 h of harvest (1 shift).
        var data = Data(worker, [Attended(worker, day, "07:00", "13:00")], [Assigned(worker, day, hanging), Assigned(worker, day, harvest, "10:00")], [harvest, hanging]);

        var figures = Calculate(worker, data);

        Assert.Equal(2m, figures.Shifts);
        Assert.Equal(300_000m + 270_000m, figures.MainPay);
    }

    // Salary type per day

    [Fact]
    public void The_salary_type_in_force_on_each_day_decides_how_the_day_is_paid()
    {
        var worker = Seasonal("Đổi", Hourly(), PerShift(new DateOnly(2026, 9, 10)));
        var job = new JobType { Name = "Thu hoạch" };
        job.Rates.Add(new PayRate { JobTypeId = job.Id, HourlyRate = 40_000, EffectiveFrom = Long });
        job.ShiftRates.Add(new ShiftRate { JobTypeId = job.Id, Amount = 300_000, EffectiveFrom = Long });
        var days = new[] { new DateOnly(2026, 9, 9), new DateOnly(2026, 9, 10) };
        var data = Data(worker, days.Select(d => Attended(worker, d, "07:00", "11:00")), days.Select(d => Assigned(worker, d, job)), [job]);

        var figures = Calculate(worker, data);

        // 9/9 by the hour: 4 h x 40,000. 9/10 per shift: 1 shift x 300,000.
        Assert.Equal([PayLineKind.Hourly, PayLineKind.Shift], figures.Jobs.Select(j => j.Kind));
        Assert.Equal(160_000m + 300_000m, figures.MainPay);
        Assert.Equal(SalaryType.PerShift, figures.SalaryType);
    }

    [Fact]
    public void A_type_change_to_staff_during_the_period_splits_the_pay()
    {
        var worker = new Employee { FullName = "Sơn" };
        worker.TypePeriods.Add(new EmployeeTypePeriod { EmployeeId = worker.Id, Type = EmployeeType.SeasonalWorker, EffectiveFrom = Long });
        worker.TypePeriods.Add(new EmployeeTypePeriod { EmployeeId = worker.Id, Type = EmployeeType.Staff, EffectiveFrom = new DateOnly(2026, 9, 10) });
        var job = ShiftJob("Thu hoạch", 300_000, Long);
        var days = new[] { new DateOnly(2026, 9, 9), new DateOnly(2026, 9, 10) };
        var data = Data(worker, days.Select(d => Attended(worker, d, "07:00", "11:00")), days.Select(d => Assigned(worker, d, job)), [job]);

        var figures = Calculate(worker, data);

        // Seasonal on 9/9: per shift by default. Staff from 9/10 with no setup: nothing is worked out and the missing setup is flagged.
        Assert.Equal(300_000m, figures.MainPay);
        Assert.True(figures.MissingSetup);
        Assert.Null(figures.SalaryType);
    }

    // Fixed salary

    [Fact]
    public void A_fixed_salary_is_paid_once_on_a_monthly_sheet_in_full()
    {
        var staff = Staff("Quản lý", Fixed(8_000_000, fieldShifts: false));

        var figures = Calculate(staff, Nothing(staff));

        Assert.Equal(8_000_000m, figures.MainPay);
        Assert.Equal(8_000_000m, figures.FixedPay);
        Assert.True(figures.OnSheet);
        Assert.False(figures.MissingSetup);
        Assert.Equal(SalaryType.FixedMonthly, figures.SalaryType);
    }

    [Fact]
    public void A_fixed_salary_is_not_paid_on_a_custom_sheet()
    {
        var staff = Staff("Quản lý", Fixed(8_000_000, fieldShifts: false));

        var figures = Calculate(staff, Nothing(staff), PayrollPeriodType.Custom);

        Assert.Equal(0m, figures.MainPay);
        Assert.False(figures.OnSheet);
    }

    [Fact]
    public void A_fixed_salary_from_the_middle_of_the_month_is_still_the_whole_amount()
    {
        var staff = Staff("Mới", Fixed(6_000_000, fieldShifts: false, from: new DateOnly(2026, 9, 20)));

        var figures = Calculate(staff, Nothing(staff));

        Assert.Equal(6_000_000m, figures.MainPay);
    }

    [Fact]
    public void A_fixed_salary_pays_the_shifts_of_the_days_with_a_work_assignment_only()
    {
        var staff = Staff("Quản lý", Fixed(8_000_000, fieldShifts: true));
        var harvest = ShiftJob("Thu hoạch", 300_000, Long);
        var (office, field) = (new DateOnly(2026, 9, 7), new DateOnly(2026, 9, 8));
        // The first day is ordinary attendance with no assignment; the second has a harvest assignment: 07:00 to 18:00 less the break, 3 shifts.
        var data = Data(staff, [Attended(staff, office, "07:00", "17:00"), Attended(staff, field, "07:00", "18:00")], [Assigned(staff, field, harvest)], [harvest], Break());

        var figures = Calculate(staff, data);

        Assert.Equal(8_000_000m, figures.MainPay);
        Assert.Equal(900_000m, figures.ShiftPay);
        Assert.Equal(3m, figures.Shifts);
        Assert.Equal([PayLineKind.FieldShift], figures.Jobs.Select(j => j.Kind));
        Assert.Empty(figures.IncompleteDays);
    }

    [Fact]
    public void A_fixed_salary_without_the_field_shift_option_earns_nothing_for_assigned_days()
    {
        var staff = Staff("Quản lý", Fixed(8_000_000, fieldShifts: false));
        var harvest = ShiftJob("Thu hoạch", 300_000, Long);
        var day = new DateOnly(2026, 9, 8);
        var data = Data(staff, [Attended(staff, day, "07:00", "18:00")], [Assigned(staff, day, harvest)], [harvest], Break());

        var figures = Calculate(staff, data);

        Assert.Equal(8_000_000m, figures.MainPay);
        Assert.Equal(0m, figures.ShiftPay);
        Assert.Empty(figures.Jobs);
    }

    [Fact]
    public void Field_shifts_of_a_fixed_salary_count_on_a_custom_sheet_without_the_salary()
    {
        var staff = Staff("Quản lý", Fixed(8_000_000, fieldShifts: true));
        var harvest = ShiftJob("Thu hoạch", 300_000, Long);
        var day = new DateOnly(2026, 9, 8);
        var data = Data(staff, [Attended(staff, day, "07:00", "18:00")], [Assigned(staff, day, harvest)], [harvest], Break());

        var figures = Calculate(staff, data, PayrollPeriodType.Custom);

        Assert.Equal(0m, figures.MainPay);
        Assert.Equal(900_000m, figures.ShiftPay);
        Assert.True(figures.OnSheet);
    }

    [Fact]
    public void An_assigned_day_of_a_fixed_salary_with_no_check_out_is_reported_but_an_ordinary_one_is_not()
    {
        var staff = Staff("Quản lý", Fixed(8_000_000, fieldShifts: true));
        var harvest = ShiftJob("Thu hoạch", 300_000, Long);
        var (office, field) = (new DateOnly(2026, 9, 7), new DateOnly(2026, 9, 8));
        var data = Data(staff, [Attended(staff, office, "07:00", null), Attended(staff, field, "07:00", null)], [Assigned(staff, field, harvest)], [harvest]);

        Assert.Equal([field], Calculate(staff, data).IncompleteDays);
    }

    // No setup

    [Fact]
    public void A_staff_employee_without_a_setup_is_on_the_sheet_with_nothing_and_a_missing_setup()
    {
        var staff = Staff("Chưa có");

        var figures = Calculate(staff, Nothing(staff));

        Assert.True(figures.MissingSetup);
        Assert.True(figures.OnSheet);
        Assert.Equal(0m, figures.MainPay);
        Assert.Null(figures.SalaryType);
    }

    [Fact]
    public void A_staff_employee_with_attendance_but_no_setup_earns_nothing_and_has_no_other_problem()
    {
        var staff = Staff("Chưa có");
        var job = ShiftJob("Thu hoạch", 300_000, Long);
        var day = new DateOnly(2026, 9, 8);
        var data = Data(staff, [Attended(staff, day, "07:00", "18:00")], [Assigned(staff, day, job)], [job]);

        var figures = Calculate(staff, data);

        Assert.Empty(figures.Jobs);
        Assert.Empty(figures.IncompleteDays);
        Assert.Empty(figures.UnratedDays);
        Assert.True(figures.MissingSetup);
    }

    [Fact]
    public void A_setup_that_starts_during_the_period_does_not_hide_the_days_before_it()
    {
        var staff = Staff("Muộn", Fixed(8_000_000, fieldShifts: true, from: new DateOnly(2026, 9, 15)));

        Assert.True(SalaryPlan.IsMissingSetup(staff, Start, End));
        Assert.False(SalaryPlan.IsMissingSetup(staff, new DateOnly(2026, 9, 15), End));
    }

    [Fact]
    public void Someone_with_a_fixed_salary_or_no_setup_belongs_on_a_sheet_of_everyone_without_attendance()
    {
        var fixedSalary = Staff("Cố định", Fixed(8_000_000, fieldShifts: true));
        var none = Staff("Chưa có");
        var seasonal = Seasonal("Thời vụ");
        var left = Staff("Đã nghỉ", Fixed(8_000_000, fieldShifts: true));
        left.StatusId = StatusIds.Employee.Left;
        left.LastWorkingDay = new DateOnly(2026, 8, 20);

        Assert.True(SalaryPlan.IsPaidWithoutAttendance(fixedSalary, Start, End));
        Assert.True(SalaryPlan.IsPaidWithoutAttendance(none, Start, End));
        Assert.False(SalaryPlan.IsPaidWithoutAttendance(seasonal, Start, End));
        Assert.False(SalaryPlan.IsPaidWithoutAttendance(left, Start, End));
    }
}
