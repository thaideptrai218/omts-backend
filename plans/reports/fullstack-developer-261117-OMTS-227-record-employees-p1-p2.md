# OMTS-227 P1+P2 backend report

Branch `feature/OMTS-227-record-employees` (3 commits, not pushed). Build 0 errors (SDK 8 docker). Migration `20261117085245_RecordEmployeesAndZoneWorkAssignments` applied to local DB (omts-sqlserver-1): existing WorkAssignment row got ZoneId = zone of its raft; Ươm job type + 30.000 rate present; tables renamed (PK/FK/IX consistent). API rebuilt, GET /health 200, login owner OK.

Tests run: RecordCrewAssignmentTests (new, 4), WorkAssignmentTests (+1 zone), HarvestCrewTests, AttendanceCorrection, PayrollSheet, WorkDayPlan, EmployeeRequest, DemoDataSeed, NurseryStartEditCancel, SeedingRecord*, unit Labor (145): all pass.

## Open points
- Payroll: no raft share exists in code (PayrollCalculator never used RaftId), so totals unchanged; "cost to zone" = assignment ZoneId, nothing else to change.
- Job types looked up by NAME (Ươm / Treo giống / Thu hoạch); renamed type => JobTypeNotInUse.
- Extra skip reason `Failed` beyond the 5 asked (locked pay period, raft not In Use, lost race).
- crewAssignment is null when the save adds no new employee (edit/correction assigns only newly added ones).
- MSG-LABOR-06 text says "raft"; for a zone place it reads "raft Z1 (zone)".
- Migration inserts Ươm only if a PayRates row exists (needs an entering account); a fresh non-demo DB gets it from the demo seed / owner.
- Seeding/nursery audit snapshots do not list the crew (harvest does).
- Attendance correction period: zone-only period allowed only for a zone the day already has.
- Not run: full test suite, SQL Server-only tests (RaftLanesMigrationSqlServerTests table name updated, untested).

## P5 (same branch)
Gate `RecordCrewAttendance` (one service) on: nursery complete (day = StartDate), seeding-complete (day = each session date), finish-harvest single + multi lane and harvest lot close (day = lot HarvestDate). MSG-LABOR-37 409 + reasonCode ATTENDANCE_OPEN + employees[{id,code,name,checkedIn}] + confirmFlag ownerApproved. Owner-only override `ownerApproved` + `approvalReason` (named apart from the lot close `reason`), audit `Record.CompletedWithOpenAttendance`. crew[].work/attendance on 3 views; lookups `assignment`. Payroll: zone assignment paid via Attendances x periods x PayRate (no raft use); unit test added. `crewAssignment` now omitted when null.
Tests: new gate tests pass. Broad run (Nursery/Seeding/Harvest/...) 666 pass / 8 fail; failures seen are in photo-required/Kestrel/cycle-without-photos and stale exact-field-list harvest tests (orderCages* fields), not touched by this work (not verified on a clean checkout). Manager-override 403 not tested.
