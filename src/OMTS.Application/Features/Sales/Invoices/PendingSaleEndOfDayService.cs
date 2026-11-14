using OMTS.Application.Common;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// The sales awaiting their VietQR transfer are stopped automatically at the end of each business day, UTC+7 (council of 03/11, PM answer 12/11/2026, OMTS-224 [3.4]): the daily job reads
/// the codes of the sales created before the day it runs for, then calls <see cref="StopAsync"/> once per code, each in its own unit of work. The stop is the one of the port
/// (<see cref="PendingSaleWriter"/>, BR-SALE-08: the code in the audit log, nothing saved on the invoice side, payments already received stay without a customer) with the system as the actor.
/// A sale a transfer, a change of payment or a stop by hand ended meanwhile is left as it ended, so a second run changes nothing.
/// </summary>
public sealed class PendingSaleEndOfDayService(IPendingSaleRepository sales, PendingSaleWriter writer, IUnitOfWork unitOfWork)
{
    /// <summary>The start of the business day <paramref name="today"/>: a sale created before it belongs to a day that has ended.</summary>
    public static DateTimeOffset StartOfDay(DateOnly today) => new(today.ToDateTime(TimeOnly.MinValue), BusinessClock.UtcOffset);

    /// <summary>The codes of the sales still awaiting their transfer that were created before the business day <paramref name="today"/> began.</summary>
    public Task<IReadOnlyList<string>> OverdueCodesAsync(DateOnly today, CancellationToken cancellationToken) =>
        sales.ListAwaitingCodesCreatedBeforeAsync(StartOfDay(today), cancellationToken);

    /// <summary>
    /// Stops the sale when it is still awaiting and was created before the day <paramref name="today"/> began; false when it ended meanwhile or is not due.
    /// </summary>
    /// <exception cref="ConcurrencyConflictException">The transfer, a change of payment or another run saved the sale after it was read here; nothing is kept, the caller reads it again.</exception>
    public async Task<bool> StopAsync(string code, DateOnly today, CancellationToken cancellationToken)
    {
        if (await sales.GetForEditAsync(code, cancellationToken) is not { Status: PendingSaleStatus.Awaiting } sale || sale.CreatedAt >= StartOfDay(today))
        {
            return false;
        }

        writer.StopAtEndOfDay(sale);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
