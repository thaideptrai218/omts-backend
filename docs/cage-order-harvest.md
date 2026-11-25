# Cage requests and harvest completion

This records the user decisions of 31 October and 11 November 2026. Requests and staffing capacity use positive whole cages. Harvest records count cages; the kg sold are the weighed kg of the harvest lot (`POST /api/harvest-lots/{id}/weigh`) and are entered on the invoice. There is no conversion from requested cages to a requested weight.

## Workflow and API

1. A linked trader sends `POST /api/me/purchase-requests` with `productId`, `requestedCages`, `pickupDate`, optional `note` and a stable `clientOperationId` for retries. The response wraps the trader DTO in `request`. `GET /api/me/purchases` returns `requests`; another trader's detail/cancellation answers 404.
2. The Owner creates a dated work plan for a raft and job, choosing themselves or a Manager. A delegated Manager must accept or decline before inviting employees. Only the accepted responsible person assigns the crew. An Owner who takes responsibility can assign directly.
3. Employees accept or refuse their shifts. The Owner accepts with `POST /api/purchase-requests/{id}/accept` only when enough distinct employees approve. The accepted cage count and pickup date must match the staffing plan. The count cannot exceed the request. Declining uses `/decline` with a reason. At 20 cages per person per day, 100 cages require five approvals.
4. A harvest lot is recorded from a line of a harvest plan and names no purchase request. One trip records one raft.
5. The Owner no longer marks the harvest of a request done: the routes `harvest-done` and `harvest-done/clear` are gone, and the mark that old requests carry (`HarvestCompletedAt`) stays readable.
6. There is no separate port weighing slip (decision of 17/11/2026): the harvest lot is weighed (Chờ cân to weighed), cage by cage since 24/11/2026 (each cage saved as it is weighed, then "Hoàn tất" gives the lot the sum), the trader confirms in person, and invoices are made from orders, not from a weighing. Existing invoice permissions, payment decisions and provenance rules still apply.
7. The trader of the lot weighs beside the cooperative and agrees (owner decision of 24/11/2026): the trader's own cages (`/api/me/weighings/{lotId}/cages`) are compared by position with the cooperative's, which stay the official weighing; once the cooperative completes, the trader agrees to its kg (`/api/me/weighings/{lotId}/agree`), and a lot of a trader is invoiced only with that agreement. A weighing completed again at another kg or time needs a new agreement.

Staff request DTOs contain `customer` and `staffing`. The trader DTO omits these internal fields. Owner decisions are nested in `response`; clients use `statusId` and `fulfilled` instead of comparing display strings.

## Persistence and concurrency

Old requests keep the paired harvest-done timestamp and account. Employee reservations and work-plan approval remain checked on the server.

The legacy `AddWorkDayPlans` migration skips creation when `EmployeesV2` has retired its old parent; rollback checks for that parent too. The later integration migration supplies the current workforce tables. Regression tests cover fresh migration and rollback/reapply against disposable SQL Server databases.

Do not apply these migrations to a shared database merely to review the PR. Verify the deployment's migration history and backup separately. This continuation ran tests against isolated databases; it did not migrate the dev database or deploy the application.

## Remaining slices

The current work-plan relation still allows one plan per request. Multiple raft plans, per-raft remaining-stock estimates and the optional reassignment of excess harvest to another trader are pending separately. No automatic surplus transfer is implemented; it needs a decision compatible with each lot belonging to one request. Real Zalo device linking, phone push delivery and the final mobile screens still require device acceptance testing.
