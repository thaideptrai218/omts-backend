# Backend integration review — 31 October 2026

This draft combines #20 (98e232c), #19 (7b67572) and #18 (0fa690b) without merging develop.

- Message-code changes are applied to the relocated controllers and individual DTO files. Old monolithic definitions remain deleted.
- Zalo technical codes remain documented compatibility codes. Zalo provider unavailability retains MSG04 because the SRS has no replacement; other errors expose canonical code/title plus legacyCode.
- Raft condition DTOs remove the two always-null harvest/lot identifiers.
- The month-end reminder test fix from 7b67572 is included.
- The cleanup migration refuses to drop legacy payroll tables if a non-Draft settlement exists. Workforce writes also refuse an unmigrated SQL database. Payroll v2 has no writer yet: its lock implementation must replace the documented empty-lock invariant when added.
- A SQL regression proves a locked settlement survives a refused migration. A model-snapshot regression checks that split/refactored features match the merged EF model.

Use a database/file copy for QA; do not reset volumes or run the cleanup on the existing tester database. A locked legacy payroll requires a separately reviewed data migration. No password reset or demo re-seed is needed for an existing QA copy.

## Approved alignment — 2 November 2026

The user approved D1–D3 in the Web decision WEB_BACKEND_21_23_ALIGNMENT_PROPOSAL_2026-10-31.md. This integration now also contains #21@6b7ce1c, #23@51420c6 and #22@8078fce (the stacked catalogue head).

- Fixed roles, readonly matrix, grants without validity dates and cooperative-wide role permissions are adopted. The existing Zalo employee-login exception remains; it grants no management permissions. Development personas use a seeded fixed role rather than creating custom roles.
- FixedRoles migration now stops before dropping columns if a stored-effective grant is expired, future-dated, linked to a discontinued role, or duplicated. It never auto-revokes grants. A SQL regression checks that each refused migration retains the schema, grant count and migration history. The existing QA preflight found one ambiguous grant; that database has not been upgraded.
- Notification v2 uses the moved interface/DTO/repository files. Existing trader, cage-request, assignment and leave producers stay registered; their previous mandatory/optional delivery policy and display-only record codes remain. Legacy text has no invented SRS message code. The cleanup's removed unrelated status groups remain removed; the notification migration restores only its three delivery statuses before adding the FK.
- Material, next-code/seed-unit, unique item names, Resume-before-edit and supplier Address/Email contracts are adopted. Supplier debt stays on permissioned debt endpoints.
- Employee profile endpoints remain Owner-only in this source, despite LABOR_WORKERS being present in the fixed manager matrix. Clients must retain their endpoint gates until the profile/scope contract is explicitly resolved.

Local verification: build has zero warnings/errors; 253 application unit tests, 2 domain tests and 849 API integration tests passed. 128 SQL/provider tests were skipped without their services; the subsequently added migration-guard test compiles and is also skipped locally. EF reports no pending model changes. No claim of SQL migration acceptance or QA3003/browser acceptance is made for this revision. CI evidence must be checked against the pushed SHA.

The existing tester database/files, develop checkout, and main API are preserved. Do not reset volumes or bypass the guard to deploy this draft. GitGuardian history findings from the previous draft are not suppressed or rewritten by this update.

Focused migration/Zalo/contract checks: 21 passed with SQL Server and Azurite. Full-suite and final CI evidence is recorded in the PR and the Web integration handoff report; a stopped/resource-starved local run is not a successful check.
