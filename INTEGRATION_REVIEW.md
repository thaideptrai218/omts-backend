# Backend integration review — 31 October 2026

This draft combines #20 (98e232c), #19 (7b67572) and #18 (0fa690b) without merging develop.

- Message-code changes are applied to the relocated controllers and individual DTO files. Old monolithic definitions remain deleted.
- Zalo technical codes remain documented compatibility codes. Zalo provider unavailability retains MSG04 because the SRS has no replacement; other errors expose canonical code/title plus legacyCode.
- Raft condition DTOs remove the two always-null harvest/lot identifiers.
- The month-end reminder test fix from 7b67572 is included.
- The cleanup migration refuses to drop legacy payroll tables if a non-Draft settlement exists. Workforce writes also refuse an unmigrated SQL database. Payroll v2 has no writer yet: its lock implementation must replace the documented empty-lock invariant when added.
- A SQL regression proves a locked settlement survives a refused migration. A model-snapshot regression checks that split/refactored features match the merged EF model.

Use a database/file copy for QA; do not reset volumes or run the cleanup on the existing tester database. A locked legacy payroll requires a separately reviewed data migration. No password reset or demo re-seed is needed for an existing QA copy.

The newer fixed-role, notifications-v2 and purchasing-catalogue branches (#21–#23) are outside this draft: their business changes require a separate compatibility review against approved client behavior.

Focused migration/Zalo/contract checks: 21 passed with SQL Server and Azurite. Full-suite and final CI evidence is recorded in the PR and the Web integration handoff report; a stopped/resource-starved local run is not a successful check.
