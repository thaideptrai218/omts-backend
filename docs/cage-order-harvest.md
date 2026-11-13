# Cage requests, harvest completion and port weighing

This records the user decisions of 31 October and 11 November 2026. Requests and staffing capacity use positive whole cages. Harvest records count cages; actual kg are recorded per cage at the port and used for invoice pricing. There is no conversion from requested cages to a requested weight.

## Workflow and API

1. A linked trader sends `POST /api/me/purchase-requests` with `productId`, `requestedCages`, `pickupDate`, optional `note` and a stable `clientOperationId` for retries. The response wraps the trader DTO in `request`. `GET /api/me/purchases` returns `requests`; another trader's detail/cancellation answers 404.
2. The Owner creates a dated work plan for a raft and job, choosing themselves or a Manager. A delegated Manager must accept or decline before inviting employees. Only the accepted responsible person assigns the crew. An Owner who takes responsibility can assign directly.
3. Employees accept or refuse their shifts. The Owner accepts with `POST /api/purchase-requests/{id}/accept` only when enough distinct employees approve. The accepted cage count and pickup date must match the staffing plan. The count cannot exceed the request. Declining uses `/decline` with a reason. At 20 cages per person per day, 100 cages require five approvals.
4. A new harvest lot must name one accepted purchase request whose harvest remains open. One trip records one raft; one request can have multiple lots. Historical lots without a request remain readable.
5. The Owner marks `/api/purchase-requests/{id}/harvest-done` with the current row version after at least one lot exists. This saves a timestamp and the account, without introducing another status. New lots are then refused. `/harvest-done/clear` reopens harvesting only while no weighing exists.
6. Staff record a port weighing from that request's own lots after harvest completion. Total cages weighed from a lot across weighings cannot exceed its recorded cages. Each cage has an actual kg value. The trader confirms the weighing before invoicing. Existing invoice permissions, payment decisions and provenance rules still apply.

Staff request DTOs contain `customer`, `staffing` and, in accepted-request detail, `harvest`. The trader DTO omits these internal fields. Owner decisions are nested in `response`; clients use `statusId` and `fulfilled` instead of comparing display strings.

## Persistence and concurrency

`PortWeighings` adds the weighing records and invoice link. `HarvestLotPurchaseRequest` adds nullable request links for old lots and the paired completion timestamp/account. Request row versions serialize recording lots, completion, clearing completion and weighing. Employee reservations and work-plan approval remain checked on the server.

The legacy `AddWorkDayPlans` migration skips creation when `EmployeesV2` has retired its old parent; rollback checks for that parent too. The later integration migration supplies the current workforce tables. Regression tests cover fresh migration and rollback/reapply against disposable SQL Server databases.

Do not apply these migrations to a shared database merely to review the PR. Verify the deployment's migration history and backup separately. This continuation ran tests against isolated databases; it did not migrate the dev database or deploy the application.

## Remaining slices

The current work-plan relation still allows one plan per request. Multiple raft plans, per-raft remaining-stock estimates and the optional reassignment of excess harvest to another trader are pending separately. No automatic surplus transfer is implemented; it needs a decision compatible with each lot belonging to one request. Real Zalo device linking, phone push delivery and the final mobile screens still require device acceptance testing.
