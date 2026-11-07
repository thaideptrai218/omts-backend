using System.Globalization;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Customers;

/// <summary>
/// SALE-04.3 – merges two profiles of the same trader, which cannot be undone. Every row of the merged profile is
/// re-pointed to the kept one (<see cref="ICustomerReferences"/>), so that its requests, invoices, payments, claims and debt
/// count with the kept profile; it keeps its own name, phone number and account. A merge never moves an account link: the
/// merged profile must have none (MSG-SALE-05). A Zone Manager confirms that the Owner agreed (MSG-SALE-04, BR-SALE-04).
/// </summary>
public sealed class CustomerMergeService(
    ICustomerRepository customers,
    ICustomerReferences references,
    CustomerDebtReader debts,
    ICurrentUser currentUser,
    PermissionService permissions,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    /// <summary>SALE-04.3 steps 2 and 3: the same refusals as the merge, then what the user is asked to confirm.</summary>
    public async Task<Result<CustomerMergePreview>> PreviewAsync(Guid keptId, Guid? mergedId, CancellationToken cancellationToken)
    {
        var pair = await LoadPairAsync(keptId, mergedId, cancellationToken);
        if (!pair.IsSuccess)
        {
            return pair.Error!;
        }

        var (kept, merged) = pair.Value;
        var needsApproval = !await IsOwnerAsync(cancellationToken);
        var owed = await debts.DebtsAsync([kept.Id, merged.Id], cancellationToken);
        return new CustomerMergePreview(
            Party(kept, owed), Party(merged, owed), await references.CountAsync(merged.Id, cancellationToken), needsApproval,
            needsApproval ? AskOwnerAgreed(kept, merged, owed).Text : null);
    }

    public async Task<Result<CustomerView>> MergeAsync(Guid keptId, MergeCustomerRequest request, CancellationToken cancellationToken)
    {
        var pair = await LoadPairAsync(keptId, request.MergedCustomerId, cancellationToken);
        if (!pair.IsSuccess)
        {
            return pair.Error!;
        }

        var (kept, merged) = pair.Value;
        if (await rowVersions.CheckAsync(kept, request.KeptRowVersion, cancellationToken) is { } keptConflict)
        {
            return keptConflict;
        }

        if (await rowVersions.CheckAsync(merged, request.MergedRowVersion, cancellationToken) is { } mergedConflict)
        {
            return mergedConflict;
        }

        if (!request.OwnerApproved && !await IsOwnerAsync(cancellationToken))
        {
            var owed = await debts.DebtsAsync([kept.Id, merged.Id], cancellationToken);
            return AppError.Conflict(AskOwnerAgreed(kept, merged, owed), new Dictionary<string, object?>
            {
                ["kept"] = Party(kept, owed),
                ["merged"] = Party(merged, owed),
                ["countedWithKept"] = await references.CountAsync(merged.Id, cancellationToken),
            });
        }

        var moved = await references.RepointAsync(merged.Id, kept.Id, cancellationToken);
        merged.MergedIntoCustomerId = kept.Id;
        // The kept profile is written too, so that a save that changes it or merges it elsewhere at this moment conflicts.
        unitOfWork.MarkChanged(kept);
        audit.Record(new AuditEntry("Customer.Merged", CustomerService.RecordType)
        {
            RecordId = merged.Id,
            RecordCode = CustomerService.AuditCode(merged),
            Before = new { MergedIntoCustomerId = (Guid?)null, merged.Name, merged.PhoneNumber },
            After = new { MergedIntoCustomerId = kept.Id, KeptName = kept.Name, KeptPhoneNumber = kept.PhoneNumber, OwnerApproved = true, Moved = moved },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        // The merged profile's invoices and payments now count with the kept one (BR-SALE-04).
        return CustomerService.View(kept, await debts.DebtAsync(kept.Id, cancellationToken));
    }

    private async Task<Result<(Customer Kept, Customer Merged)>> LoadPairAsync(Guid keptId, Guid? mergedId, CancellationToken cancellationToken)
    {
        if (mergedId is not { } id || id == keptId)
        {
            return AppError.Invalid("Customer profile to merge");
        }

        var kept = await customers.GetForEditAsync(keptId, cancellationToken);
        var merged = await customers.GetForEditAsync(id, cancellationToken);
        if (kept is null || merged is null)
        {
            return AppError.NotFound;
        }

        foreach (var profile in new[] { kept, merged })
        {
            if (profile.MergedIntoCustomerId is not null)
            {
                return AppError.Conflict(UserMessages.WrongStatus($"{CustomerService.ObjectName} {profile.Name}", CustomerService.MergedStatus));
            }
        }

        if (merged.AccountId is not null)
        {
            var bothLinked = kept.AccountId is not null;
            return AppError.Conflict(
                UserMessages.CustomerMergeRefused(merged.Name, kept.Name, bothLinked),
                new Dictionary<string, object?> { ["canKeepMergedInstead"] = !bothLinked });
        }

        return (kept, merged);
    }

    private async Task<bool> IsOwnerAsync(CancellationToken cancellationToken) =>
        currentUser.AccountId is { } accountId && await permissions.GetAccessAsync(accountId, cancellationToken) is { IsCooperativeOwner: true };

    private static UserMessage AskOwnerAgreed(Customer kept, Customer merged, IReadOnlyDictionary<Guid, decimal> debts) => UserMessages.CustomerMergeNeedsOwnerAgreement(
        merged.Name, merged.PhoneNumber ?? "–", Money(debts[merged.Id]),
        kept.Name, kept.PhoneNumber ?? "–", Money(debts[kept.Id]));

    private static CustomerMergeParty Party(Customer c, IReadOnlyDictionary<Guid, decimal> debts) =>
        new(c.Id, c.Name, c.PhoneNumber, debts[c.Id], c.AccountId is not null, RowVersionGuard.Encode(c.RowVersion));

    private static string Money(decimal amount) => amount.ToString("#,##0", CultureInfo.InvariantCulture);
}
