using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Sales.Customers;

/// <summary>
/// The customer settings of the cooperative: the debt warning and the fields a customer profile must have. Read by whoever views customers (the customer form and the invoice form
/// read them), saved by the Owner only (API filter). They live on the cooperative row, like the harvest settings, so saving them also makes an open profile form stale, and the
/// other way round.
/// </summary>
public sealed class CustomerSettingsService(ICooperativeRepository cooperatives, RowVersionGuard rowVersions, AuditLogService audit, IUnitOfWork unitOfWork)
{
    /// <summary>The column holds 18 digits.</summary>
    public const decimal MaxDebtAmount = 999_999_999_999m;

    public const int MaxDebtDays = 3650;

    public async Task<Result<CustomerSettingsView>> GetAsync(CancellationToken cancellationToken) =>
        await cooperatives.GetCurrentAsync(cancellationToken) is { } cooperative ? View(cooperative) : AppError.NotFound;

    public async Task<Result<CustomerSettingsView>> UpdateAsync(UpdateCustomerSettingsRequest request, CancellationToken cancellationToken)
    {
        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken);
        if (cooperative is null)
        {
            return AppError.NotFound;
        }

        var invalid = Validate(request);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (await rowVersions.CheckAsync(cooperative, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var before = Snapshot(cooperative);
        var required = request.RequiredFields!;
        cooperative.CustomerDebtWarningEnabled = request.DebtWarningEnabled!.Value;
        cooperative.CustomerMaxDebtAmount = request.MaxDebtAmount;
        cooperative.CustomerMaxDebtDays = request.MaxDebtDays;
        cooperative.CustomerAllowMoreDebtOverLimit = request.AllowMoreDebtOverLimit!.Value;
        cooperative.CustomerNoDebtForWalkIn = request.NoDebtForWalkIn!.Value;
        cooperative.CustomerRequirePhoneNumber2 = required.PhoneNumber2;
        cooperative.CustomerRequireEmail = required.Email;
        cooperative.CustomerRequireGender = required.Gender;
        cooperative.CustomerRequireBirthDate = required.BirthDate;
        cooperative.CustomerRequireAddress = required.Address;
        cooperative.CustomerRequireFacebook = required.Facebook;
        cooperative.CustomerRequireBank = required.Bank;
        cooperative.CustomerRequireTaxCode = required.TaxCode;
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(cooperative));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("Cooperative.CustomerSettingsChanged", nameof(Cooperative)) { RecordId = cooperative.Id, Before = changedBefore, After = changedAfter });
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return View(cooperative);
    }

    private static List<string> Validate(UpdateCustomerSettingsRequest request)
    {
        var invalid = new List<string>();
        if (request.DebtWarningEnabled is null)
        {
            invalid.Add("Debt warning");
        }

        if (request.AllowMoreDebtOverLimit is null)
        {
            invalid.Add("Allow more debt over the limit");
        }

        if (request.NoDebtForWalkIn is null)
        {
            invalid.Add("No debt for walk-in customer");
        }

        if (request.RequiredFields is null)
        {
            invalid.Add("Required fields");
        }

        if (request.MaxDebtAmount is { } amount && (amount is <= 0m or > MaxDebtAmount || decimal.Truncate(amount) != amount))
        {
            invalid.Add("Debt amount");
        }

        if (request.MaxDebtDays is { } days && days is < 1 or > MaxDebtDays)
        {
            invalid.Add("Debt days");
        }

        // A warning with no limit would never show.
        if (request.DebtWarningEnabled == true && request.MaxDebtAmount is null && request.MaxDebtDays is null)
        {
            invalid.Add("Debt limit");
        }

        return invalid;
    }

    /// <summary>The settings as saved; shared with the code that reads them for the profile checks and the debt limit.</summary>
    internal static CustomerSettingsView View(Cooperative cooperative) => new(
        cooperative.CustomerDebtWarningEnabled,
        cooperative.CustomerMaxDebtAmount,
        cooperative.CustomerMaxDebtDays,
        cooperative.CustomerAllowMoreDebtOverLimit,
        cooperative.CustomerNoDebtForWalkIn,
        RequiredOf(cooperative),
        RowVersionGuard.Encode(cooperative.RowVersion));

    internal static CustomerRequiredFields RequiredOf(Cooperative cooperative) => new(
        cooperative.CustomerRequirePhoneNumber2,
        cooperative.CustomerRequireEmail,
        cooperative.CustomerRequireGender,
        cooperative.CustomerRequireBirthDate,
        cooperative.CustomerRequireAddress,
        cooperative.CustomerRequireFacebook,
        cooperative.CustomerRequireBank,
        cooperative.CustomerRequireTaxCode);

    private static object Snapshot(Cooperative c) => new
    {
        c.CustomerDebtWarningEnabled, c.CustomerMaxDebtAmount, c.CustomerMaxDebtDays, c.CustomerAllowMoreDebtOverLimit, c.CustomerNoDebtForWalkIn,
        c.CustomerRequirePhoneNumber2, c.CustomerRequireEmail, c.CustomerRequireGender, c.CustomerRequireBirthDate, c.CustomerRequireAddress, c.CustomerRequireFacebook,
        c.CustomerRequireBank, c.CustomerRequireTaxCode,
    };
}
