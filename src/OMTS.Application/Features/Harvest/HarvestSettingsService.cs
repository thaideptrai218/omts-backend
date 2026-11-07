using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Farming;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// HARVEST-03 set the two harvest settings of the cooperative, Cooperative Owner only (BR-HARVEST-06). A new average is used only for later lots, which copy it; new days apply to every lot
/// still for sale from the next daily close check. The settings live on the cooperative row, so saving them also makes an open profile form stale, and the other way round.
/// </summary>
public sealed class HarvestSettingsService(
    ICooperativeRepository cooperatives, IHarvestLotRepository lots, RowVersionGuard rowVersions, AuditLogService audit, IUnitOfWork unitOfWork, IHarvestAutoCloseSchedule schedule)
{
    public const string AverageKgPerCageField = "Average kg per cage";
    public const string DaysBeforeAutoCloseField = "Days before automatic close";

    /// <summary>The column holds 9 digits with 2 decimals.</summary>
    public const decimal MaxAverageKgPerCage = 9_999_999.99m;

    /// <summary>The SRS sets no upper limit (PM default): one is needed so a date can be worked out from the days.</summary>
    public const int MaxDaysBeforeAutoClose = 365;

    public async Task<Result<HarvestSettingsView>> GetAsync(CancellationToken cancellationToken) =>
        await cooperatives.GetCurrentAsync(cancellationToken) is { } cooperative ? View(cooperative) : AppError.NotFound;

    public async Task<Result<HarvestSettingsSaved>> UpdateAsync(UpdateHarvestSettingsRequest request, CancellationToken cancellationToken)
    {
        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken);
        if (cooperative is null)
        {
            return AppError.NotFound;
        }

        var check = new FieldCheck();
        check.Check(request.AverageKgPerCage is > 0m and <= MaxAverageKgPerCage && decimal.Round(request.AverageKgPerCage.Value, 2) == request.AverageKgPerCage.Value, AverageKgPerCageField);
        check.Check(DaysAreValid(request.DaysBeforeAutoClose), DaysBeforeAutoCloseField);
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        if (await rowVersions.CheckAsync(cooperative, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var before = Snapshot(cooperative);
        cooperative.AverageKgPerCage = request.AverageKgPerCage!.Value;
        cooperative.DaysBeforeAutoClose = request.DaysBeforeAutoClose!.Value;
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(cooperative));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("Cooperative.HarvestSettingsChanged", nameof(Cooperative)) { RecordId = cooperative.Id, Before = changedBefore, After = changedAfter });
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        var saved = UserMessages.Saved("Harvest settings");
        return new HarvestSettingsSaved(View(cooperative), saved.Text, saved.Code);
    }

    /// <summary>
    /// HARVEST-03 step 4. The next check (today's before its run time at the start of the day, tomorrow's after it) closes a lot whose day it counts from, plus the days, is before
    /// that check's date (GFR-05).
    /// </summary>
    public async Task<Result<AutoClosePreview>> PreviewAsync(int? days, CancellationToken cancellationToken)
    {
        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken);
        if (cooperative is null)
        {
            return AppError.NotFound;
        }

        if (!DaysAreValid(days))
        {
            return AppError.Invalid(DaysBeforeAutoCloseField);
        }

        var count = await lots.CountForSaleSinceAtMostAsync(HarvestLotAutoCloseRule.LatestSinceClosedOn(schedule.NextCheckDate(), days!.Value), cancellationToken);
        var lowered = days < cooperative.DaysBeforeAutoClose;
        var question = lowered ? UserMessages.AutoCloseDaysLowered(days.Value, count) : null;
        return new AutoClosePreview(days.Value, count, lowered, question?.Text, question?.Code);
    }

    private static bool DaysAreValid(int? days) => days is >= 1 and <= MaxDaysBeforeAutoClose;

    private static HarvestSettingsView View(Cooperative cooperative) =>
        new(cooperative.AverageKgPerCage, cooperative.DaysBeforeAutoClose, RowVersionGuard.Encode(cooperative.RowVersion));

    private static object Snapshot(Cooperative cooperative) => new { cooperative.AverageKgPerCage, cooperative.DaysBeforeAutoClose };
}
