using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Accounts;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// ACS-01: two trader accounts, one Active and one Locked. Self-registration needs a verification code
/// from an SMS, so the account and its customer profile are built here the way <c>RegistrationService</c>
/// builds them (verified phone, same customer linking, same audit row); the Locked one is then locked as the
/// System Administrator would. Matched by username.
/// </summary>
internal sealed class DemoTraderStep : IDemoSeedStep
{
    private static readonly (string Username, string DisplayName, string Phone, bool Locked)[] Traders =
    [
        ("thuonglai1", "Nguyễn Thị Hương (thương lái Hạ Long)", "0900000021", false),
        ("thuonglai2", "Trần Văn Dũng (thương lái Móng Cái)", "0900000022", true),
    ];

    public string Name => "trader accounts";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var hash = context.Get<IPasswordHasher>().Hash(DemoSeedContext.Password);
        var linker = context.Get<TraderCustomerLinker>();
        var audit = context.Get<AuditLogService>();
        var created = 0;
        foreach (var (username, displayName, phoneNumber, locked) in Traders)
        {
            if (await context.Db.Accounts.AnyAsync(a => a.Username == username, cancellationToken))
            {
                continue;
            }

            var trader = new Account
            {
                SubjectType = SubjectType.Trader,
                StatusId = locked ? StatusIds.Account.Locked : StatusIds.Account.Active,
                Username = username,
                DisplayName = displayName,
                PhoneNumber = PhoneNumbers.Normalize(phoneNumber),
                PhoneVerifiedAt = context.Now,
                PasswordHash = hash,
            };
            context.Db.Accounts.Add(trader);
            var (customer, _) = await linker.LinkNewTraderAsync(trader, context.CooperativeId, cancellationToken);
            audit.Record(new AuditEntry("Account.Registered", nameof(Account))
            {
                RecordId = trader.Id,
                RecordCode = trader.Username,
                ActorAccountId = trader.Id,
                After = new { trader.Username, trader.SubjectType, trader.PhoneNumber, CustomerId = customer.Id },
            });
            if (locked)
            {
                audit.RecordStatusChange(nameof(Account), trader.Id, trader.Username, StatusIds.Account.Active, StatusIds.Account.Locked, "Tài khoản demo bị khóa");
            }

            await context.Db.SaveChangesAsync(cancellationToken);
            created++;
        }

        return created;
    }
}
