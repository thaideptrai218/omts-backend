using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Features.Purchasing.Items;

/// <summary>
/// The input VAT rates of the cooperative ("Thuế GTGT" of the settings). Reading follows the item catalogue; every write is Owner only (API filter) and audited. The percent of a rate can be edited
/// (documents made afterwards use it, documents made before keep the percent they took); the system rate KCT cannot be edited, deactivated or made the default; deactivating a rate that items still
/// have needs a replacement, and those items move to it. A rate is never deleted, so the lines made with it keep naming it.
/// </summary>
public sealed class VatRateService(
    IVatRateRepository rates, InputItemAccess access, RowVersionGuard rowVersions, AuditLogService audit, IUnitOfWork unitOfWork)
{
    private const string RecordType = nameof(VatRate);
    private const string ObjectName = "VAT rate";

    public async Task<Result<IReadOnlyList<VatRateView>>> ListAsync(bool? active, CancellationToken cancellationToken)
    {
        if (!await access.MayReadAsync(cancellationToken))
        {
            return AppError.Forbidden;
        }

        var views = await ViewsAsync(cancellationToken);
        return Result<IReadOnlyList<VatRateView>>.Success(active is { } only ? views.Where(v => v.Active == only).ToList() : views);
    }

    public async Task<Result<VatRateView>> CreateAsync(SaveVatRateRequest request, CancellationToken cancellationToken)
    {
        if (VatRateRules.InvalidFields(request.Name, request.Percent) is { Count: > 0 } invalid)
        {
            return AppError.InvalidFields(invalid);
        }

        var name = request.Name!.Trim();
        var all = await rates.GetAllForEditAsync(cancellationToken);
        if (all.Count >= VatRateRules.MaxRates)
        {
            return AppError.Invalid("Name", $"At most {VatRateRules.MaxRates} VAT rates.");
        }

        if (await rates.FindByNameAsync(name, null, cancellationToken) is { } same)
        {
            return AppError.AlreadyExists(ObjectName, same.Id, "Name");
        }

        var rate = new VatRate { Name = name, Percent = request.Percent, SortOrder = all.Select(r => r.SortOrder).DefaultIfEmpty(-1).Max() + 1 };
        rates.Add(rate);
        audit.Record(new AuditEntry("VatRate.Created", RecordType) { RecordId = rate.Id, RecordCode = rate.Name, After = new { rate.Name, rate.Percent } });
        if (await SaveAsync(name, cancellationToken) is { } error)
        {
            return error;
        }

        return View(rate, 0);
    }

    /// <summary>Changes the name and the percent. 409 MSG-GEN-09 on the system rate. Existing document lines keep the percent they took.</summary>
    public async Task<Result<VatRateView>> UpdateAsync(Guid rateId, SaveVatRateRequest request, CancellationToken cancellationToken)
    {
        var rate = await rates.GetForEditAsync(rateId, cancellationToken);
        if (rate is null)
        {
            return AppError.NotFound;
        }

        if (VatRateRules.InvalidFields(request.Name, request.Percent) is { Count: > 0 } invalid)
        {
            return AppError.InvalidFields(invalid);
        }

        if (rate.IsSystem)
        {
            return Refused(VatRateRules.SystemRateText(rate.Name), VatRateRules.SystemRate);
        }

        if (await rowVersions.CheckAsync(rate, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var name = request.Name!.Trim();
        if (await rates.FindByNameAsync(name, rate.Id, cancellationToken) is { } same)
        {
            return AppError.AlreadyExists(ObjectName, same.Id, "Name");
        }

        if (name != rate.Name || request.Percent != rate.Percent)
        {
            audit.Record(new AuditEntry("VatRate.Updated", RecordType)
            {
                RecordId = rate.Id, RecordCode = name, Before = new { rate.Name, rate.Percent }, After = new { Name = name, request.Percent },
            });
            rate.Name = name;
            rate.Percent = request.Percent;
            if (await SaveAsync(name, cancellationToken) is { } error)
            {
                return error;
            }
        }

        return await ViewOfAsync(rate.Id, cancellationToken);
    }

    /// <summary>Makes the rate the one a new supply item starts with. 409 MSG-GEN-09 when it is KCT or deactivated.</summary>
    public async Task<Result<VatRateView>> SetDefaultAsync(Guid rateId, CancellationToken cancellationToken)
    {
        var all = await rates.GetAllForEditAsync(cancellationToken);
        var rate = all.FirstOrDefault(r => r.Id == rateId);
        if (rate is null)
        {
            return AppError.NotFound;
        }

        if (rate.IsSystem || rate.IsExempt)
        {
            return Refused(VatRateRules.ExemptNotDefaultText(rate.Name), VatRateRules.ExemptNotDefault);
        }

        if (!rate.IsActive)
        {
            return Refused(VatRateRules.InactiveNotDefaultText(rate.Name), VatRateRules.InactiveNotDefault);
        }

        if (!rate.IsDefault)
        {
            var previous = all.FirstOrDefault(r => r.IsDefault);
            audit.Record(new AuditEntry("VatRate.DefaultChanged", RecordType)
            {
                RecordId = rate.Id, RecordCode = rate.Name, Before = new { Default = previous?.Name }, After = new { Default = rate.Name },
            });
            if (previous is not null)
            {
                previous.IsDefault = false;
                unitOfWork.MarkChanged(previous);
            }

            rate.IsDefault = true;
            unitOfWork.MarkChanged(rate);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await ViewOfAsync(rate.Id, cancellationToken);
    }

    /// <summary>
    /// Takes the rate out of the pickers. 409 MSG-GEN-09 on KCT and on the default rate; 400 MSG-GEN-01 on <c>replacementId</c> when items still have the rate and no active rate other than it is named.
    /// The items that have the rate (deleted ones too) move to the replacement.
    /// </summary>
    public async Task<Result<DeactivateVatRateResult>> DeactivateAsync(Guid rateId, DeactivateVatRateRequest request, CancellationToken cancellationToken)
    {
        var all = await rates.GetAllForEditAsync(cancellationToken);
        var rate = all.FirstOrDefault(r => r.Id == rateId);
        if (rate is null)
        {
            return AppError.NotFound;
        }

        if (await rowVersions.CheckAsync(rate, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        if (rate.IsSystem)
        {
            return Refused(VatRateRules.SystemRateText(rate.Name), VatRateRules.SystemRate);
        }

        if (!rate.IsActive)
        {
            return new DeactivateVatRateResult(await ViewOfAsync(rate.Id, cancellationToken), 0);
        }

        if (rate.IsDefault)
        {
            return Refused(VatRateRules.DefaultNotDeactivatedText(rate.Name), VatRateRules.DefaultNotDeactivated);
        }

        var itemCount = (await rates.ItemCountsAsync(cancellationToken)).GetValueOrDefault(rate.Id);
        var replacement = request.ReplacementId is { } replacementId ? all.FirstOrDefault(r => r.Id == replacementId) : null;
        if (VatRateRules.ReplacementProblem(rate, itemCount, request.ReplacementId, replacement))
        {
            return AppError.Invalid("replacementId");
        }

        // Deleted items follow the rate too, so no item is left on a rate that is out of the pickers.
        var target = replacement ?? all.First(r => r.IsDefault);
        var moved = await rates.ItemsOfAsync(rate.Id, cancellationToken);
        foreach (var item in moved)
        {
            item.PurchaseVatRateId = target.Id;
            unitOfWork.MarkChanged(item);
        }

        rate.IsActive = false;
        audit.Record(new AuditEntry("VatRate.Deactivated", RecordType)
        {
            RecordId = rate.Id, RecordCode = rate.Name, Before = new { Active = true }, After = new { Active = false, MovedItems = itemCount, Replacement = itemCount > 0 ? target.Name : null },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new DeactivateVatRateResult(await ViewOfAsync(rate.Id, cancellationToken), itemCount);
    }

    public async Task<Result<VatRateView>> ActivateAsync(Guid rateId, CancellationToken cancellationToken)
    {
        var rate = await rates.GetForEditAsync(rateId, cancellationToken);
        if (rate is null)
        {
            return AppError.NotFound;
        }

        if (!rate.IsActive)
        {
            rate.IsActive = true;
            audit.Record(new AuditEntry("VatRate.Activated", RecordType) { RecordId = rate.Id, RecordCode = rate.Name, Before = new { Active = false }, After = new { Active = true } });
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await ViewOfAsync(rate.Id, cancellationToken);
    }

    private static AppError Refused(string text, string reasonCode) =>
        AppError.Conflict(UserMessages.VatRateRefused(text), new Dictionary<string, object?> { ["reasonCode"] = reasonCode });

    private async Task<AppError?> SaveAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DuplicateRecordException)
        {
            unitOfWork.DiscardChanges();
            return await rates.FindByNameAsync(name, null, cancellationToken) is { } other
                ? AppError.AlreadyExists(ObjectName, other.Id, "Name")
                : AppError.Conflict(UserMessages.AlreadyExists(ObjectName));
        }
    }

    private async Task<VatRateView> ViewOfAsync(Guid rateId, CancellationToken cancellationToken) =>
        (await ViewsAsync(cancellationToken)).First(v => v.Id == rateId);

    private async Task<IReadOnlyList<VatRateView>> ViewsAsync(CancellationToken cancellationToken)
    {
        var counts = await rates.ItemCountsAsync(cancellationToken);
        return (await rates.ListAsync(cancellationToken)).Select(r => View(r, counts.GetValueOrDefault(r.Id))).ToList();
    }

    private static VatRateView View(VatRate rate, int itemCount) => new(
        rate.Id, rate.Name, rate.Percent, rate.IsExempt, rate.IsSystem, rate.IsDefault, rate.IsActive, rate.SortOrder, itemCount, RowVersionGuard.Encode(rate.RowVersion));
}
