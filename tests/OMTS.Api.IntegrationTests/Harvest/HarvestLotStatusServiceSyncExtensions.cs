using OMTS.Application.Common.Results;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>
/// The status changes of a lot as the tests of the status rules call them: in one expression inside the lambdas that run a change on a tracked lot. The service is async (a close and a reopen write the product
/// ledger); a test has no synchronization context, so waiting for it is safe.
/// </summary>
internal static class HarvestLotStatusServiceSyncExtensions
{
    public static AppError? Close(this HarvestLotStatusService statuses, HarvestLot lot, DateOnly date, decimal leftoverKg, HarvestLotCloseReason? reason, Guid? actorAccountId = null) =>
        statuses.CloseAsync(lot, date, leftoverKg, reason, actorAccountId).GetAwaiter().GetResult();

    public static AppError? CloseSoldWholeLot(this HarvestLotStatusService statuses, HarvestLot lot, DateOnly date, Guid salesInvoiceId) =>
        statuses.CloseSoldWholeLotAsync(lot, date, salesInvoiceId).GetAwaiter().GetResult();

    public static bool Reopen(this HarvestLotStatusService statuses, HarvestLot lot, DateOnly date, HarvestLotReopenCause cause, Guid? salesInvoiceId = null) =>
        statuses.ReopenAsync(lot, date, cause, salesInvoiceId).GetAwaiter().GetResult();
}
