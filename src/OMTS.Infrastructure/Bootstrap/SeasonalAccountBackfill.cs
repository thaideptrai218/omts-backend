using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Employees;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Infrastructure.Bootstrap;

/// <summary>
/// The seed step that gives the seasonal workers of before the seasonal accounts (owner decision of 25/11/2026) their login: a Staff account with the fixed initial password
/// (<see cref="SeasonalWorkerAccounts.InitialPassword"/>, changed at the first login, never expiring) and the EMPLOYEE role at All zones. Idempotent: it only touches an Employed
/// seasonal worker who has no account. A worker with no phone number, or whose phone number or username another account already holds, is skipped and logged. It never fails the
/// seed: on any fault it says so and leaves the rows as they are for the next run.
/// </summary>
public sealed class SeasonalAccountBackfill(
    OmtsDbContext db, IPasswordHasher passwordHasher, AuditLogService audit, TimeProvider timeProvider, ILogger<SeasonalAccountBackfill> logger)
{
    public async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var owner = await db.Accounts.AsNoTracking().OrderBy(a => a.CreatedAt)
                .FirstOrDefaultAsync(a => a.SubjectType == SubjectType.CooperativeOwner && a.StatusId == StatusIds.Account.Active, cancellationToken);
            var role = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Code == FixedRoles.EmployeeCode, cancellationToken);
            if (owner is null || role is null)
            {
                return "Seasonal accounts: skipped (no Cooperative Owner or no EMPLOYEE role yet).";
            }

            var today = timeProvider.Today();
            var candidates = (await db.Employees.Include(e => e.TypePeriods)
                    .Where(e => e.AccountId == null && e.StatusId == StatusIds.Employee.Working && (e.LastWorkingDay == null || e.LastWorkingDay >= today))
                    .OrderBy(e => e.CreatedAt).ToListAsync(cancellationToken))
                .Where(e => EmployeeTypePeriod.TypeOn(e.TypePeriods, today) == EmployeeType.SeasonalWorker)
                .ToList();
            if (candidates.Count == 0)
            {
                return "Seasonal accounts: nothing to create.";
            }

            var usernames = (await db.Accounts.Select(a => a.Username).ToListAsync(cancellationToken)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var phones = (await db.Accounts.Where(a => a.PhoneNumber != null).Select(a => a.PhoneNumber!).ToListAsync(cancellationToken)).ToHashSet();
            // One hash for all: the password is the same for every seasonal account.
            var hash = passwordHasher.Hash(SeasonalWorkerAccounts.InitialPassword);
            var (created, skipped) = (0, 0);
            foreach (var employee in candidates)
            {
                var username = SeasonalAccountProvisioner.Suggestions(employee.Code, employee.PhoneNumber).FirstOrDefault(u => !usernames.Contains(u));
                if (employee.PhoneNumber is null || phones.Contains(employee.PhoneNumber) || username is null)
                {
                    skipped++;
                    logger.LogWarning(
                        "No seasonal account for employee {Code}: {Reason}", employee.Code,
                        employee.PhoneNumber is null ? "no phone number" : phones.Contains(employee.PhoneNumber) ? "the phone number belongs to another account" : "no free username");
                    continue;
                }

                Add(employee, username, hash, role, owner.Id);
                usernames.Add(username);
                phones.Add(employee.PhoneNumber);
                created++;
            }

            await db.SaveChangesAsync(cancellationToken);
            return $"Seasonal accounts: {created} created, {skipped} skipped (no phone number, or the phone number or username is taken).";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A fault here must not stop the deployment: the step is idempotent and runs again with the next seed.
            db.ChangeTracker.Clear();
            logger.LogWarning(exception, "The seasonal account backfill failed; it runs again with the next seed");
            return "Seasonal accounts: could not be created this time; they are created on the next seed.";
        }
    }

    private void Add(Employee employee, string username, string hash, Role role, Guid grantedBy)
    {
        var account = new Account
        {
            SubjectType = SubjectType.Staff,
            StatusId = StatusIds.Account.Active,
            DisplayName = employee.FullName,
            Username = username,
            PhoneNumber = employee.PhoneNumber,
            PasswordHash = hash,
            MustChangePassword = true,
            TemporaryPasswordIssuedAt = SeasonalWorkerAccounts.NeverExpiresIssuedAt,
        };
        var grant = new AccessGrant
        {
            AccountId = account.Id, RoleId = role.Id, ZoneId = null, ScopeType = AccessScopeType.AllZones, GrantedByAccountId = grantedBy,
        };
        db.Accounts.Add(account);
        db.AccessGrants.Add(grant);
        employee.AccountId = account.Id;
        audit.Record(new AuditEntry("Account.Created", nameof(Account))
        {
            RecordId = account.Id,
            RecordCode = account.Username,
            Reason = "Seasonal worker account created by the seed.",
            After = new { account.DisplayName, account.Username, account.PhoneNumber, account.SubjectType, EmployeeId = employee.Id, account.MustChangePassword },
        });
        audit.Record(new AuditEntry("AccessGrant.Granted", nameof(AccessGrant))
        {
            RecordId = grant.Id, RecordCode = account.Username, After = new { AccountId = account.Id, Role = role.Code, grant.ZoneId },
        });
    }
}
