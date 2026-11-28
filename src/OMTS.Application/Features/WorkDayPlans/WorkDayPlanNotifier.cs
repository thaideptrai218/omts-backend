using System.Globalization;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.WorkDayPlans;

/// <summary>
/// The in-app notices of a work-day plan, saved with the change. The person in charge hears when the plan is full, short again or short after invitations expired; an invited employee is told of the invitation and
/// when it did not succeed. Nobody is told of their own action.
/// </summary>
public sealed class WorkDayPlanNotifier(INotificationDispatcher notifications)
{
    private const string StaffingTitle = "Kế hoạch ngày làm";

    public static string WorkOf(WorkDayPlan plan) => plan.Kind switch
    {
        WorkDayPlanKind.Harvest => "thu hoạch",
        WorkDayPlanKind.Nursery => "ươm giống",
        _ => "thả giống",
    };

    public static string PlaceOf(WorkDayPlan plan) => plan.Raft is { } raft ? $"bè {raft.Code}" : $"vùng {plan.Zone.Code}";

    private static string DayOf(WorkDayPlan plan) => plan.WorkDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private string Head(WorkDayPlan plan) => $"Kế hoạch {WorkOf(plan)} {plan.Code} ngày {DayOf(plan)}, {PlaceOf(plan)}";

    /// <summary>The employees are asked to take part; each account is told once. An employee without an account is not told: the Zone Manager or the Owner calls them and confirms for them.</summary>
    public Task InvitedAsync(WorkDayPlan plan, IReadOnlyCollection<Guid> employeeAccountIds, DateTimeOffset deadline, CancellationToken cancellationToken) =>
        Send(employeeAccountIds, NotificationTypes.WorkDayPlanInvited, "Lời mời làm việc", plan,
            $"{Head(plan)}: bạn được mời tham gia. Hạn trả lời {deadline.ToOffset(TimeSpan.FromHours(7)):HH:mm dd/MM/yyyy}.", cancellationToken);

    /// <summary>A pending invitation ended because the plan has all the people it needs.</summary>
    public Task UnsuccessfulAsync(WorkDayPlan plan, IReadOnlyCollection<Guid> employeeAccountIds, CancellationToken cancellationToken) =>
        Send(employeeAccountIds, NotificationTypes.WorkDayPlanInvited, "Lời mời không thành công", plan,
            $"{Head(plan)} đã đủ người nên lời mời của bạn không thành công.", cancellationToken);

    public Task ReadyAsync(WorkDayPlan plan, Guid? actor, CancellationToken cancellationToken) =>
        Responsible(plan, actor, "Đã đủ người", $"{Head(plan)} đã đủ người ({WorkDayPlanRules.Present(plan)}/{plan.RequiredPeople}).", cancellationToken);

    public Task ShortAgainAsync(WorkDayPlan plan, Guid? actor, CancellationToken cancellationToken) =>
        Responsible(plan, actor, "Chưa đủ người", $"{Head(plan)} chưa đủ người: còn thiếu {WorkDayPlanRules.Missing(plan)}.", cancellationToken);

    /// <summary>Invitations expired and the plan is still short: "còn thiếu n".</summary>
    public Task ExpiredAsync(WorkDayPlan plan, int expired, CancellationToken cancellationToken) =>
        Responsible(plan, null, "Lời mời hết hạn", $"{Head(plan)}: {expired} lời mời hết hạn, còn thiếu {WorkDayPlanRules.Missing(plan)} người.", cancellationToken);

    public Task CancelledAsync(WorkDayPlan plan, Guid? actor, CancellationToken cancellationToken) =>
        Responsible(plan, actor, "Kế hoạch đã hủy", $"{Head(plan)} đã bị hủy.", cancellationToken);

    private Task Responsible(WorkDayPlan plan, Guid? actor, string title, string text, CancellationToken cancellationToken) =>
        plan.ResponsibleAccountId == actor
            ? Task.CompletedTask
            : Send([plan.ResponsibleAccountId], NotificationTypes.WorkDayPlanStaffing, $"{StaffingTitle}: {title}", plan, text, cancellationToken);

    private Task Send(IReadOnlyCollection<Guid> recipients, string type, string title, WorkDayPlan plan, string text, CancellationToken cancellationToken) =>
        recipients.Count == 0
            ? Task.CompletedTask
            : notifications.NotifyAsync(recipients, type, title, text, nameof(WorkDayPlan), plan.Code, false, cancellationToken, plan.Id);
}
