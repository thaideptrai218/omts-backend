using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class VerificationCodeRepository(OmtsDbContext db) : IVerificationCodeRepository
{
    public void Add(VerificationCode code) => db.VerificationCodes.Add(code);

    public Task<VerificationCode?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.VerificationCodes.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<VerificationCode?> GetByTokenHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        db.VerificationCodes.FirstOrDefaultAsync(c => c.TokenHash == tokenHash, cancellationToken);

    public async Task<IReadOnlyList<VerificationCode>> GetRecentAsync(
        VerificationPurpose purpose, Guid? accountId, string target, DateTimeOffset since, CancellationToken cancellationToken) =>
        await db.VerificationCodes
            .Where(c => c.Purpose == purpose && c.CreatedAt >= since)
            .Where(c => accountId != null ? c.AccountId == accountId : c.AccountId == null && c.Target == target)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(cancellationToken);
}
