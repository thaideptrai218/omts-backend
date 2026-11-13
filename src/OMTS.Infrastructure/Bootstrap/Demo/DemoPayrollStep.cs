using Microsoft.EntityFrameworkCore;
using OMTS.Application.Features.Labor.Payrolls;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// LABOR-04, LABOR-12 to LABOR-14: attendance of the demo seasonal workers over the last three months (so the payroll screens have hours to
/// show), then, through the services the API uses: last month's before sheet Finalized with one payslip partly paid in cash and one paid in
/// full by transfer (each with its cash voucher in the cash book), one advance to the worker paid in full, and last month's sheet Provisional,
/// whose payslip previews that advance. Demo data only, run by <c>seed --bootstrap --demo</c>, never by the production seed. Idempotent: when
/// any payroll sheet has payslips the step leaves everything alone, so a sheet a person made, or a rerun, is never doubled. The two empty
/// headers older demo seeds made (Finalized or Awaiting finalization, no payslips) are replaced by these sheets.
/// </summary>
internal sealed class DemoPayrollStep : IDemoSeedStep
{
    private const int DaysOfAttendance = 100;

    public string Name => "attendance and payroll";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        if (await context.Db.Payrolls.AnyAsync(p => p.Lines.Any(), cancellationToken))
        {
            return 0;
        }

        var thisMonth = new DateOnly(context.Today.Year, context.Today.Month, 1);
        var created = await AddAttendanceAsync(context, thisMonth, cancellationToken);
        created += await RemoveEmptyHeadersAsync(context, cancellationToken);

        var payrolls = context.Get<PayrollService>();
        var paying = context.Get<PayrollPaymentService>();
        var (monthBefore, lastMonth) = (thisMonth.AddMonths(-2), thisMonth.AddMonths(-1));
        var first = DemoSeedContext.Ensure(
            await payrolls.CreateAsync(new CreatePayrollRequest(PayrollPeriodType.Monthly, monthBefore, lastMonth.AddDays(-1), PayrollScope.All, null), cancellationToken),
            "the payroll of the month before last");
        var finalized = DemoSeedContext.Ensure(
            await payrolls.FinalizeAsync(first.Id, new FinalizePayrollRequest($"{monthBefore:yyyy-MM}"), cancellationToken), "finalizing the payroll of the month before last");
        created++;

        // The first payslip is paid in part in cash; the second in full by transfer, so that worker owes nothing and the advance below is an advance.
        var slips = finalized.Slips.OrderByDescending(s => s.Net).ToList();
        if (slips.Count > 0)
        {
            var part = Math.Max(1, Math.Floor(slips[0].Net / 2 / 1000) * 1000);
            DemoSeedContext.Ensure(
                await paying.PaySheetAsync(
                    finalized.Id, new PayPayrollRequest(context.Now, PaymentMethod.Cash, "Trả đợt 1", [new PayPayrollLine(slips[0].Id, part)]), null, cancellationToken),
                "the partial wage payment");
            created++;
        }

        var slipIds = slips.Select(s => s.EmployeeId).ToList();
        var working = (await context.Db.Employees.Where(e => slipIds.Contains(e.Id) && e.StatusId == StatusIds.Employee.Working).Select(e => e.Id).ToListAsync(cancellationToken)).ToHashSet();
        if (slips.Skip(1).FirstOrDefault(s => working.Contains(s.EmployeeId)) is { } full)
        {
            DemoSeedContext.Ensure(
                await paying.PaySheetAsync(
                    finalized.Id, new PayPayrollRequest(context.Now, PaymentMethod.BankTransfer, "Chuyển khoản đủ", [new PayPayrollLine(full.Id, full.Net)]), null, cancellationToken),
                "the full wage payment");
            DemoSeedContext.Ensure(
                await paying.PayEmployeeAsync(new StandalonePayrollPaymentRequest(full.EmployeeId, 500_000, context.Now, PaymentMethod.Cash, "Tạm ứng lương"), null, cancellationToken),
                "the advance");
            created++;
        }

        DemoSeedContext.Ensure(
            await payrolls.CreateAsync(new CreatePayrollRequest(PayrollPeriodType.Monthly, lastMonth, thisMonth.AddDays(-1), PayrollScope.All, null), cancellationToken),
            "the payroll of last month");
        return created + 1;
    }

    /// <summary>Removes the headers without payslips that earlier demo seeds made; no endpoint can make a Finalized or Awaiting finalization sheet without payslips.</summary>
    private static async Task<int> RemoveEmptyHeadersAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var empty = await context.Db.Payrolls
            .Where(p => !p.Lines.Any() && (p.StatusId == StatusIds.Payroll.Finalized || p.StatusId == StatusIds.Payroll.AwaitingFinalization))
            .ToListAsync(cancellationToken);
        context.Db.Payrolls.RemoveRange(empty);
        await context.Db.SaveChangesAsync(cancellationToken);
        return 0;
    }

    /// <summary>
    /// A day of work on most days of the last <see cref="DaysOfAttendance"/> days for each seasonal worker with a job type on the profile, on the
    /// first raft of their zone: a full day, a morning, or a day with a change of job; the day off of a week moves with the worker. Days the worker
    /// had left, or already has a record, are skipped.
    /// </summary>
    private static async Task<int> AddAttendanceAsync(DemoSeedContext context, DateOnly thisMonth, CancellationToken cancellationToken)
    {
        var first = thisMonth.AddMonths(-3);
        var last = context.Today.AddDays(-1);
        var created = 0;
        foreach (var plan in DemoEmployeePlans.All.Where(p => p.Type == EmployeeType.SeasonalWorker && p.JobTypes is { Length: > 0 }))
        {
            var employee = await context.Db.Employees.Include(e => e.TypePeriods)
                .FirstOrDefaultAsync(e => plan.Phone == null ? e.FullName == plan.FullName && e.PhoneNumber == null : e.PhoneNumber == plan.Phone, cancellationToken);
            var zoneCode = plan.WorkingZones[0];
            var zone = await context.Db.FarmingZones.FirstOrDefaultAsync(z => z.Code == zoneCode, cancellationToken);
            var raft = zone is null ? null : await context.Db.Rafts.FirstOrDefaultAsync(r => r.ZoneId == zone.Id && r.Code == DemoRaftStep.RaftCode(zoneCode, 1), cancellationToken);
            var jobs = await DemoLaborSettingsStep.JobTypesInUseAsync(context, plan.JobTypes!, cancellationToken);
            if (employee is null || raft is null || jobs.Count == 0)
            {
                continue;
            }

            var existing = (await context.Db.Attendances.Where(a => a.EmployeeId == employee.Id && a.Date >= first).Select(a => a.Date).ToListAsync(cancellationToken)).ToHashSet();
            var offset = employee.Id.GetHashCode() & 7;
            for (var day = first; day <= last; day = day.AddDays(1))
            {
                var seed = day.DayNumber + offset;
                if (seed % 7 == 0 || existing.Contains(day) || (employee.LastWorkingDay is { } end && day > end)
                    || EmployeeTypePeriod.TypeOn(employee.TypePeriods, day) != EmployeeType.SeasonalWorker)
                {
                    continue;
                }

                AddDay(context, employee, raft.Id, jobs, day, seed);
                created++;
            }
        }

        await context.Db.SaveChangesAsync(cancellationToken);
        return created > 0 ? 1 : 0;
    }

    private static void AddDay(DemoSeedContext context, Employee employee, Guid raftId, IReadOnlyList<Guid> jobs, DateOnly day, int seed)
    {
        var morning = seed % 5 == 1;
        var checkIn = At(day, 7, 0);
        var checkOut = morning ? At(day, 11, 30) : At(day, seed % 3 == 0 ? 16 : 15, seed % 2 == 0 ? 30 : 0);
        context.Db.Attendances.Add(new Attendance
        {
            EmployeeId = employee.Id, Date = day, CheckInAt = checkIn, CheckOutAt = checkOut,
            CheckInMethod = AttendanceMethod.Manual, CheckOutMethod = AttendanceMethod.Manual,
            CheckInRecordedByAccountId = context.OwnerId, CheckOutRecordedByAccountId = context.OwnerId,
        });
        context.Db.WorkAssignments.Add(new WorkAssignment
        {
            EmployeeId = employee.Id, Date = day, RaftId = raftId, JobTypeId = jobs[seed % jobs.Count], AssignedByAccountId = context.OwnerId,
        });
        if (jobs.Count > 1 && !morning && seed % 4 == 0)
        {
            context.Db.WorkAssignments.Add(new WorkAssignment
            {
                EmployeeId = employee.Id, Date = day, RaftId = raftId, JobTypeId = jobs[(seed + 1) % jobs.Count], AssignedByAccountId = context.OwnerId, StartsAt = At(day, 13, 30),
            });
        }
    }

    private static DateTimeOffset At(DateOnly day, int hour, int minute) =>
        new(day.Year, day.Month, day.Day, hour, minute, 0, TimeSpan.FromHours(7));
}
