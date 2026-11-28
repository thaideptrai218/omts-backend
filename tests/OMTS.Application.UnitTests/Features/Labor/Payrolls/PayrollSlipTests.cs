using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Labor.Payrolls;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Labor.Payrolls;

/// <summary>The payslip made from the figures: the sums of the adjustments are kept through a recalculation, the advances come off what is earned, and a sheet is not ready while a salary setup is missing.</summary>
public sealed class PayrollSlipTests
{
    private static readonly DateOnly Start = new(2026, 9, 1);
    private static readonly DateOnly End = new(2026, 9, 30);

    private sealed class FakeSheets : IPayrollSheetRepository
    {
        public List<object> Removed { get; } = [];
        public List<object> Added { get; } = [];

        public Task<Payroll?> GetForEditAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Payroll?> GetForEditByLineAsync(Guid lineId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Payroll>> ListActiveOverlappingAsync(DateOnly from, DateOnly to, Guid? exceptId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Add(Payroll payroll) => Added.Add(payroll);

        public void Add(PayrollLine line) => Added.Add(line);

        public void Add(PayrollLineJob job) => Added.Add(job);

        public void Add(PayrollLineAdjustment adjustment) => Added.Add(adjustment);

        public void Remove(PayrollLine line) => Removed.Add(line);

        public void Remove(PayrollLineJob job) => Removed.Add(job);

        public void Remove(PayrollLineAdjustment adjustment) => Removed.Add(adjustment);
    }

    private sealed class FakePayments(params OpenAdvance[] advances) : IWagePaymentRepository
    {
        public Task<IReadOnlyList<PayrollLine>> ListFinalizedLinesAsync(Guid employeeId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, decimal>> PaidAmountsAsync(IReadOnlyCollection<Guid> lineIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<OpenAdvance>> ListOpenAdvancesAsync(IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OpenAdvance>>(advances.Where(a => employeeIds.Contains(a.Advance.EmployeeId)).ToList());

        public Task<WagePayment?> GetByVoucherForEditAsync(Guid voucherId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<decimal> PaidBeforeAsync(WagePayment payment, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<decimal> DeductedAsync(Guid advanceId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<WagePayment>> ListByRequestKeyAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Add(WagePayment payment) => throw new NotSupportedException();

        public void Add(PayrollAdvanceDeduction deduction) => throw new NotSupportedException();
    }

    private sealed class FakeNumbers : IDocumentNumberAllocator
    {
        private long next;

        public Task<long> NextAsync(string key, CancellationToken cancellationToken) => Task.FromResult(++next);
    }

    private static Employee Staff(string name)
    {
        var employee = new Employee { FullName = name };
        employee.TypePeriods.Add(new EmployeeTypePeriod { EmployeeId = employee.Id, Type = EmployeeType.Staff, EffectiveFrom = new DateOnly(2026, 1, 1) });
        return employee;
    }

    private static Employee WithFixedSalary(Employee employee, decimal amount)
    {
        employee.SalarySetups.Add(new EmployeeSalarySetup
        {
            EmployeeId = employee.Id, Type = SalaryType.FixedMonthly, MonthlyAmount = amount, PayFieldShifts = true, EffectiveFrom = new DateOnly(2026, 1, 1),
        });
        return employee;
    }

    private static SlipFigures Figures(Employee employee) => PayrollCalculator.Calculate(
        employee, Start, End, PayrollPeriodType.Monthly, new PayrollWorkData([employee], [], [], [], new Dictionary<Guid, JobType>()));

    private static PayrollLineSync Sync(FakeSheets sheets, params OpenAdvance[] advances) => new(sheets, new FakePayments(advances), new FakeNumbers());

    private static Payroll SheetWith(PayrollLine line, int status = StatusIds.Payroll.Draft)
    {
        var sheet = new Payroll { PeriodType = PayrollPeriodType.Monthly, PeriodStart = Start, PeriodEnd = End, StatusId = status };
        line.PayrollId = sheet.Id;
        sheet.Lines.Add(line);
        return sheet;
    }

    private static PayrollLine LineWith(Employee employee, params (PayrollAdjustmentKind Kind, decimal Amount)[] adjustments)
    {
        var line = new PayrollLine { EmployeeId = employee.Id, Employee = employee, Code = "PL000001" };
        foreach (var (kind, amount) in adjustments)
        {
            line.Adjustments.Add(new PayrollLineAdjustment { PayrollLineId = line.Id, Kind = kind, Amount = amount, Reason = "Lý do" });
        }

        PayrollLineSync.SumAdjustments(line);
        return line;
    }

    [Fact]
    public async Task A_new_slip_takes_the_fixed_salary_and_the_shift_pay_into_its_net()
    {
        var staff = WithFixedSalary(Staff("Quản lý"), 8_000_000);
        var sheet = new Payroll { PeriodType = PayrollPeriodType.Monthly, PeriodStart = Start, PeriodEnd = End };

        await Sync(new FakeSheets()).SyncAsync(sheet, [Figures(staff)], dropEmpty: true, CancellationToken.None);

        var line = Assert.Single(sheet.Lines);
        Assert.Equal((SalaryType.FixedMonthly, 8_000_000m, 0m), (line.SalaryType, line.MainPay, line.ShiftPay));
        Assert.Equal(8_000_000m, line.Net);
    }

    [Fact]
    public async Task The_adjustments_of_a_slip_are_kept_when_the_sheet_is_recalculated()
    {
        var staff = WithFixedSalary(Staff("Quản lý"), 8_000_000);
        var line = LineWith(staff, (PayrollAdjustmentKind.Bonus, 500_000), (PayrollAdjustmentKind.Bonus, 100_000), (PayrollAdjustmentKind.Deduction, 200_000));
        var sheet = SheetWith(line);
        var sheets = new FakeSheets();

        await Sync(sheets).SyncAsync(sheet, [Figures(staff)], dropEmpty: true, CancellationToken.None);

        Assert.Same(line, Assert.Single(sheet.Lines));
        Assert.Equal(3, line.Adjustments.Count);
        Assert.Empty(sheets.Removed.OfType<PayrollLineAdjustment>());
        Assert.Equal((600_000m, 200_000m), (line.Bonus, line.ManualDeduction));
        Assert.Equal(8_400_000m, line.Income);
        Assert.Equal(8_400_000m, line.Net);
    }

    [Fact]
    public async Task A_slip_with_adjustments_stays_on_the_sheet_when_its_employee_has_nothing_to_pay_any_more()
    {
        var staff = Staff("Quản lý");
        staff.SalarySetups.Add(new EmployeeSalarySetup { EmployeeId = staff.Id, Type = SalaryType.Hourly, EffectiveFrom = new DateOnly(2026, 1, 1) });
        var line = LineWith(staff, (PayrollAdjustmentKind.Bonus, 300_000));
        var sheet = SheetWith(line);
        sheet.PeriodType = PayrollPeriodType.Custom;

        // An hourly employee with no hours has no pay, but the bonus is still the Owner's.
        var figures = PayrollCalculator.Calculate(staff, Start, End, PayrollPeriodType.Custom, new PayrollWorkData([staff], [], [], [], new Dictionary<Guid, JobType>()));
        await Sync(new FakeSheets()).SyncAsync(sheet, [figures], dropEmpty: true, CancellationToken.None);

        Assert.Same(line, Assert.Single(sheet.Lines));
        Assert.Equal(300_000m, line.Net);
    }

    [Fact]
    public async Task A_slip_without_adjustments_is_dropped_when_its_employee_has_nothing_to_pay_any_more()
    {
        var staff = Staff("Quản lý");
        staff.SalarySetups.Add(new EmployeeSalarySetup { EmployeeId = staff.Id, Type = SalaryType.Hourly, EffectiveFrom = new DateOnly(2026, 1, 1) });
        var sheet = SheetWith(LineWith(staff));
        var sheets = new FakeSheets();

        await Sync(sheets).SyncAsync(sheet, [Figures(staff)], dropEmpty: true, CancellationToken.None);

        Assert.Empty(sheet.Lines);
    }

    [Fact]
    public async Task An_advance_comes_off_the_income_of_a_staff_slip_and_never_more_than_it()
    {
        var staff = WithFixedSalary(Staff("Quản lý"), 8_000_000);
        var advance = new OpenAdvance(new WagePayment { EmployeeId = staff.Id, Kind = WagePaymentKind.Advance, Amount = 9_000_000 }, 9_000_000);
        var sheet = new Payroll { PeriodType = PayrollPeriodType.Monthly, PeriodStart = Start, PeriodEnd = End };

        await Sync(new FakeSheets(), advance).SyncAsync(sheet, [Figures(staff)], dropEmpty: true, CancellationToken.None);

        var line = Assert.Single(sheet.Lines);
        Assert.Equal(8_000_000m, line.Deduction);
        Assert.Equal(0m, line.Net);
    }

    [Theory]
    [InlineData(8_000_000, 500_000, 500_000)]
    [InlineData(300_000, 500_000, 300_000)]
    [InlineData(-100_000, 500_000, 0)]
    [InlineData(1_000_000, 0, 0)]
    public void The_advance_deduction_is_the_balance_up_to_the_income(decimal income, decimal balance, decimal expected) =>
        Assert.Equal(expected, PayrollLineSync.AdvanceDeductionOf(income, balance));

    [Fact]
    public void A_staff_employee_without_a_salary_setup_blocks_finalizing()
    {
        var staff = Staff("Chưa có");
        var line = LineWith(staff);
        var sheet = SheetWith(line);

        var (reasons, items) = PayrollService.NotReady(sheet, [Figures(staff)], End.AddDays(1));

        Assert.Equal(["Chưa có: Chưa thiết lập lương"], reasons);
        Assert.Equal(["NoSalarySetup"], items.Select(i => i.Kind));
        Assert.Equal(staff.Id, items[0].EmployeeId);
    }

    [Fact]
    public void A_sheet_of_staff_with_salaries_is_ready_once_its_period_has_ended()
    {
        var staff = WithFixedSalary(Staff("Quản lý"), 8_000_000);
        var sheet = SheetWith(LineWith(staff));

        Assert.Empty(PayrollService.NotReady(sheet, [Figures(staff)], End.AddDays(1)).Reasons);
        Assert.Single(PayrollService.NotReady(sheet, [Figures(staff)], End).Reasons);
    }

    [Fact]
    public void A_slip_whose_deductions_exceed_its_income_blocks_finalizing()
    {
        var staff = WithFixedSalary(Staff("Quản lý"), 1_000_000);
        var line = LineWith(staff, (PayrollAdjustmentKind.Deduction, 1_500_000));
        var sheet = SheetWith(line);

        var (_, items) = PayrollService.NotReady(sheet, [Figures(staff)], End.AddDays(1));

        Assert.Equal(["NegativePay"], items.Select(i => i.Kind));
    }
}
