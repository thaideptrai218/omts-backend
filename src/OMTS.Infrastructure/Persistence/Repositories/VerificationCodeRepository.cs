using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class VerificationCodeRepository(OmtsDbContext db) : IVerificationCodeRepository
{
    public void Add(VerificationCode code) => db.VerificationCodes.Add(code);

    public void Remove(VerificationCode code) => db.VerificationCodes.Remove(code);

    public Task<IReadOnlyList<DateTimeOffset>> GetSendTimesToTargetAsync(string target, DateTimeOffset since, CancellationToken cancellationToken) =>
        SendTimesAsync(db.VerificationCodes.Where(c => c.Target == target), since, cancellationToken);

    public async Task<int> CountSmsSendsAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        await db.VerificationCodes.Where(c => c.Channel == VerificationChannel.Sms && c.LastSentAt >= since).SumAsync(c => (int?)c.SendCount, cancellationToken) ?? 0;

    public Task<IReadOnlyList<DateTimeOffset>> GetSendTimesAsync(
        VerificationPurpose purpose, string target, DateTimeOffset since, CancellationToken cancellationToken) =>
        SendTimesAsync(db.VerificationCodes.Where(c => c.Purpose == purpose && c.Target == target), since, cancellationToken);

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

    public async Task<IReadOnlyList<VerificationCode>> ListOpenAsync(
        VerificationPurpose purpose, string target, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.VerificationCodes
            .Where(c => c.Purpose == purpose && c.Target == target && c.ConsumedAt == null && c.CancelledAt == null && c.ExpiresAt > now)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<VerificationCode>> ListUsableOfAccountAsync(
        Guid accountId, VerificationChannel channel, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.VerificationCodes
            .Where(c => c.AccountId == accountId && c.Channel == channel && c.CancelledAt == null && c.TokenConsumedAt == null)
            .Where(c => c.ExpiresAt > now || c.TokenExpiresAt > now)
            .ToListAsync(cancellationToken);

    private static async Task<IReadOnlyList<DateTimeOffset>> SendTimesAsync(
        IQueryable<VerificationCode> requests, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var times = await requests.AsNoTracking().Where(c => c.LastSentAt > since).Select(c => c.SendTimes).ToListAsync(cancellationToken);
        return times.SelectMany(sent => sent).Where(sent => sent > since).Order().ToList();
    }
}
