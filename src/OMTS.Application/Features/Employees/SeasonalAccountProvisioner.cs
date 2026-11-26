using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Accounts;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Employees;

/// <summary>
/// Gives a seasonal worker the login account every one of them has (owner decision of 25/11/2026): a Staff account with the phone number and
/// name of the profile, the fixed initial password (<see cref="SeasonalWorkerAccounts.InitialPassword"/>, changed at the first login) and the
/// EMPLOYEE role at All zones, which is what lets the account sign in. The username is the one given, else the lower-case employee code
/// (NV000012 gives nv000012), else "tv" and the digits of the phone number. Nothing is saved: the caller saves the account with the profile.
/// </summary>
public sealed class SeasonalAccountProvisioner(StaffAccountService staffAccounts, IAccountRepository accounts)
{
    public async Task<Result<StaffAccountWithPassword>> ProvisionAsync(Employee employee, string? username, string? email, CancellationToken cancellationToken)
    {
        if (employee.PhoneNumber is null)
        {
            return AppError.Invalid("Phone number");
        }

        var chosen = username ?? await FreeUsernameAsync(employee, cancellationToken);
        return chosen is null
            ? AppError.Invalid("Username")
            : await staffAccounts.AddStaffAccountAsync(
                new CreateStaffAccountRequest(employee.FullName, chosen, employee.PhoneNumber, email, null, [new GrantDraft(null, Role: FixedRoles.EmployeeCode)]),
                employee,
                cancellationToken,
                SeasonalWorkerAccounts.InitialPassword);
    }

    /// <summary>The first username of the suggestions that no account has, or null when all are taken.</summary>
    public async Task<string?> FreeUsernameAsync(Employee employee, CancellationToken cancellationToken)
    {
        foreach (var candidate in Suggestions(employee.Code, employee.PhoneNumber))
        {
            if (!await accounts.UsernameExistsAsync(candidate, null, cancellationToken))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The usernames suggested for a seasonal worker, best first; each is valid as a username.</summary>
    public static IReadOnlyList<string> Suggestions(string? code, string? phone)
    {
        var digits = new string((phone ?? string.Empty).Where(char.IsDigit).ToArray());
        return new[] { code, digits.Length > 0 ? $"tv{digits}" : null }
            .Select(UsernameGenerator.Normalize)
            .OfType<string>()
            .Distinct()
            .ToList();
    }
}
