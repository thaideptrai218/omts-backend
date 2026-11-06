# omts-backend

Backend API for **OMTS — Oyster Management and Traceability System** for Trung Nam Aquaculture Cooperative (SEP490_G83).
Sibling repos: [omts-web](https://github.com/thaideptrai218/omts-web) · [omts-mobile](https://github.com/thaideptrai218/omts-mobile)

## Stack

.NET 8 · ASP.NET Core Web API · EF Core · SQL Server · xUnit

## Structure (Clean Architecture)

```
src/
  OMTS.Domain/                 No dependencies.
    Common/                    Entity, AuditableEntity, IHasStatus
    Constants/                 StatusIds, StatusGroups (fixed Status catalog)
    Enums/
    Entities/<Group>/          Entities by group: Identity, Farming, Cultivation, Purchasing, Sales,
                               Labor, Platform
  OMTS.Application/            Use cases and ports. Depends on Domain only (no EF Core).
    Common/Interfaces/         Ports: Persistence/ (IUnitOfWork, I*Repository), Identity/, ICurrentUser
    Common/Models/             Types the ports exchange (AccessToken, EffectiveGrant, ...)
    Common/Constants/
    Features/<Feature>/        Services of a feature, with Dtos/ (API contracts) and Models/
  OMTS.Infrastructure/         Implements the ports. Depends on Application.
    Identity/                  JWT tokens, password hashing
    Persistence/               OmtsDbContext, UnitOfWork
      Configurations/<Group>/  One <Entity>Configuration per entity, same groups as Domain
      Repositories/  Interceptors/  Seed/  Migrations/
  OMTS.Api/                    HTTP endpoints, DI composition root.
    Controllers/  Filters/  Extensions/  Services/  Common/
tests/
  OMTS.Domain.UnitTests/  OMTS.Application.UnitTests/
  OMTS.Api.IntegrationTests/   Infrastructure/ (test host, test-only endpoints), one folder per feature
```

Dependencies point inward only; tests in `tests/` guard this rule. Conventions: one type per file,
namespace = folder path, interfaces (ports) live under `Application/Common/Interfaces`.

## Run with Docker (web, mobile and QA)

Only Docker Desktop (or Docker Engine with Compose v2) is needed: no .NET SDK, SQL Server or user-secrets. Ports 8080
and 1433 of the PC must be free. A local SQL Server default instance already listens on 1433: stop it first.

```bash
cp .env.example .env            # cmd.exe: copy .env.example .env. Local-only values; .env is git-ignored, never commit it
docker compose up -d --build    # first run builds the image and seeds the database (a few minutes)
```

Compose starts SQL Server, runs `seed --bootstrap --demo` once (migrations, the cooperative and the demo data of an
oyster cooperative in Vân Đồn, Quảng Ninh) and then starts the API in Development. The seed is idempotent: it runs again
on every `docker compose up` and creates nothing twice.

| What | Where |
|---|---|
| API | `http://localhost:8080` |
| Swagger | `http://localhost:8080/swagger` |
| Health | `http://localhost:8080/health/db` (database), `http://localhost:8080/health/ready` (deployment complete) |
| SQL Server (this PC only) | `localhost,1433`, user `sa`, password `MSSQL_SA_PASSWORD` from `.env` |

**Demo logins.** Every account below uses the password `Demo@1234`. It exists only in this local demo data; a shared
server uses a secret of its own (see [Demo on a shared server](#demo-on-a-shared-server)).

| Username | Role | Zone | What happens at login |
|---|---|---|---|
| `owner` | Cooperative Owner | all | Normal login |
| `quanlyvung1` | Zone Manager | DEMO-01 | Normal login |
| `quanlyvung2` | Zone Manager | DEMO-02 | Normal login |
| `ketoan` | Accountant | all zones | Normal login |
| `thuonglai1` | Trader (Active) | – | Normal login |
| `thuonglai2` | Trader (Locked) | – | Refused: the account is locked |
| `nhanvien.doimk` | Zone Manager | DEMO-01 | Logged in with `mustChangePassword = true` (change it first) |
| `nhanvien.chuaxacthuc` | Zone Manager | DEMO-02 | Logged in with `phoneVerificationRequired = true` (verify the phone first) |
| `nhanvien.hethan` | Zone Manager | DEMO-01, pair revoked | Refused: no Effective zone–role pair (MSG-ACCOUNT-07) |

A password you change stays changed: the seed never resets it. The Owner gets `Demo@1234` only while still on the
bootstrap temporary password. To get every demo login back, reset the environment with `docker compose down -v`.

The demo data also holds the cooperative profile (with a logo, a cover photo and two certifications: one Valid, one
expiring within 30 days), a fictional receiving bank account (`DEMO00000001`; skipped with a message when `Payments:Provider` does not accept it, as `None` does not), the zones `DEMO-01`/`DEMO-02` (In Use) and `DEMO-03` (Discontinued), zone licenses (`DEMO-01`:
an expired license replaced by one expiring within 30 days; `DEMO-02`: a valid license and an older expired one), about 10 rafts with lanes per zone (the first five of each In Use zone with an Open cultivation cycle, the first three with cultivation logs by the zone manager), 9 employees (one Left, one without a phone number),
5 suppliers, 15 farming input items (codes VT001–VT015, every seed item counted in `con`) and 20 purchase receipts of the last 3 months
(16 Completed, 2 Cancelled, 2 Draft): Material lines and two seed receipts name the raft they were bought for, one line has a
fractional quantity (its amount is not a whole thousand), with a return and a return the Owner cancelled (`TH` codes). Payments to the
suppliers (`TT` codes, not linked to
receipts), one amount of money paid back and one payment cancelled feed the supplier debts (`GET /api/supplier-debts`). The Owner's
list holds a MSG-PURCHASE-01 notification for each completed receipt and a MSG-PURCHASE-04 for each transaction.
The demo data also holds 4 products (one Discontinued), 7 customer profiles (2 linked to the trader accounts, 2 the same buyer ready to merge) and
4 purchase requests of `thuonglai1`, one in each state (Awaiting response, Accepted for fewer kg with pay later, Declined, Cancelled).
An answer sends an SMS, so with an SMS sender other than Fake or None the requests stay unanswered and the seed output says so;
such a sender gets no mandatory SMS of the seed either (new purchase requests, water alerts).
`DEMO-01` and `DEMO-02` have water thresholds (pH 7.5–8.5, salinity 18–32 ‰, temperature 15–32 °C) and one water
measurement a day for the 30 days before the seed day, going round the rafts In Use of the zone, each with its temperature
and a placeholder meter photo: three readings outside the thresholds, one corrected reading (a salinity of 2.6 ‰ fixed to
26.0 ‰) and one voided reading. The mistyped 2.6 ‰ alerted before its correction, so the Zone Managers' inboxes hold four
sets of water alerts (the demo records as the Owner, who is not told of their own action). Phone numbers are fake
(`0900 000 0xx`).

Dates are set relative to the day of the first seed and are not moved afterwards:
- After a few weeks the "expiring" license and certification have expired. `docker compose down -v` gives fresh dates.
- Receipt codes follow one sequence in the order they were seeded (`PN000001`…`PN000020`), not the receipt date, and so do
  payment codes (`TT000001`…) and return codes (`TH000001`…).
- The debt age and the current-month statement of a supplier move with the calendar; pass `from`/`to` to the statement.
- The last water measurement is from the day before the seed: a month later without new ones, the zones are overdue and
  get the 06:00 reminder (MSG-WATER-06).
- The receipt list shows the current month by default (PURCHASE-03): pass `from`/`to` to see the older demo receipts.

**Verification codes.** SMS, email and push use the fake providers: nothing leaves the machine. What would have been
sent stays in memory until the API restarts. Read it in the dev inbox, also listed in Swagger:
- `GET /api/dev/sms?phoneNumber=`
- `GET /api/dev/email?to=`
- `GET /api/dev/push?accountId=`

`docker compose logs -f api` shows each fake message's id and masked recipient, never the code.

For example, `nhanvien.chuaxacthuc` (phone `0900000015`) logs in with `phoneVerificationRequired = true`, and its
business calls answer MSG-ACCOUNT-04 until the phone is verified. Logging in sends no code; with that login's access token:
1. `POST /api/me/phone-verification` with `{}` sends the code.
2. `GET /api/dev/sms?phoneNumber=0900000015` shows it.
3. `POST /api/verifications/{id}/verify` with `{ "code": "…" }` returns a verification token.
4. `POST /api/me/phone-verification/confirm` with `{ "verificationToken": "…" }` completes it.

The dev inbox is anonymous, so anyone on the same network can read the codes. Run this environment only on a network
you trust, never on public Wi-Fi.

**Phone on the same Wi-Fi.** The API listens on every interface of the PC. Find the PC's LAN IP (`ipconfig` on Windows,
`ipconfig getifaddr en0` on macOS) and point the mobile app at `http://<LAN IP>:8080`. If the phone cannot connect, allow
inbound TCP 8080 in the PC's firewall. The Android emulator reaches the PC at `http://10.0.2.2:8080`. File links (license
documents, raft drawings, supplier certificates) are relative, signed paths such as `/api/files/local/...?expires=…&sig=…`.
Prefix them with the API base URL.

**Web app.** In Development `Cors:AllowedOrigins` allows `http://localhost:3000`, so the Next.js dev server can call the
API from the browser. For another origin, add a line such as `Cors__AllowedOrigins__1: http://192.168.1.10:3000` under
`environment` in `docker-compose.yml`, then run `docker compose up -d`.

**Update, reset, stop.**
- `docker compose up -d --build`: after a pull. It rebuilds the image, and the seed applies new migrations.
- `docker compose down -v`: deletes the database and the stored files. The next `up` seeds a fresh copy.
- `docker compose down`: stops everything and keeps the data.

## Getting started

Prerequisites: .NET SDK 8.0 (pinned by `global.json`), SQL Server (LocalDB / Docker / Azure SQL).

```bash
dotnet restore
# Connection string is kept out of git via user-secrets:
dotnet user-secrets set "ConnectionStrings:OmtsDb" "Server=localhost;Database=OMTS;User Id=sa;Password=<pwd>;TrustServerCertificate=True" --project src/OMTS.Api
# JWT signing key (random, at least 32 bytes), e.g. from: openssl rand -base64 64
dotnet user-secrets set "Jwt:SigningKey" "<random key>" --project src/OMTS.Api
dotnet run --project src/OMTS.Api        # Swagger at /swagger, health at /health
dotnet test                               # all tests
```

API integration tests use an in-memory database. Tests marked `[SqlServerFact]` (migrations, constraints,
transactions) need a real SQL Server and are skipped unless `OMTS_TEST_SQLSERVER` holds a server connection
string; each test creates and drops its own database. CI starts a SQL Server container for them.

```bash
OMTS_TEST_SQLSERVER="Server=localhost;User Id=sa;Password=<pwd>;TrustServerCertificate=True" dotnet test
```

### Authentication

`POST /api/auth/login` (username, verified phone or verified email) returns a 15-minute access token and a
refresh token (7 days web / 30 days mobile, rotated on every `POST /api/auth/refresh`); `POST /api/auth/logout`
revokes the session. `GET /api/me` and `GET /api/me/permissions` return the caller and their effective
zone-scoped permissions. Errors are RFC 7807 problem details whose `title` and `code` are the message code of
the SRS 27/10/2026 catalogue (`MSG-GEN-01`, `MSG-ACCOUNT-05`…) and whose `detail` is the SRS English text. For one
release they also carry `legacyCode`, the old code of the same situation (`MSG01`, `MSG12`…), so web and mobile can
switch on their own schedule. A few old codes have no SRS 27/10 message yet and are still sent as `code` (MSG05,
MSG06, MSG19, MSG58, the technical codes `NOT_FOUND`, `UNEXPECTED_ERROR`…; full list in
`MessageCodes.Legacy.KeptAsCode`). Success responses that carry a text carry its code next to it (`messageCode`,
`noticeCode`).

Dates and times are ISO 8601. A time sent without a UTC offset (`2026-10-20T08:00:00`, in JSON or a multipart field)
is Vietnam time (UTC+7), never the server's time zone or UTC; `Z` or an offset such as `+07:00` is kept. Responses always
carry the offset. Business dates (`from`/`to` filters, receipt dates) are Vietnam dates.

### Roles and zone access

OMTS has three fixed roles (BR-FARM-07): Cooperative Owner, Zone Manager and Accountant. They are not created, edited or
discontinued: `GET /api/roles` lists the two staff roles with their `code` (`ZONE_MANAGER`, `ACCOUNTANT`) and their
permissions, the static matrix of SRS §I.4.4 in `FixedRoles`. The Owner grants a staff account zone–role pairs
(`POST /api/accounts/{id}/grants` with `role` (code) or `roleId`, and a `zoneId` or none for All zones). A pair is Effective
until it is revoked with a reason; there are no validity dates. On records with a zone a role works only in the zones of
its pairs; records with no zone (suppliers, purchase receipts and returns, supplier payments and debts, customers,
payroll…) follow the role only, so an Accountant pair for one zone opens them (BR-FARM-08). The rafts a Zone Manager names on
a purchase receipt line are still those of the zones of their Zone Manager pairs. A staff account without an Effective pair
cannot log in.

### Farming zones and licenses

- Zones (FARM-02): `GET /api/zones/next-code` (Owner) proposes the next code `KV01`, `KV02`… (GBR-03: the highest `KVnn` in
  use, discontinued zones included, plus one); the Owner may save another code. A Discontinued zone is read-only: its
  edit answers MSG-GEN-09 until `POST /api/zones/{id}/resume` (`/reactivate` is the older route, kept for one release).
  The water measurement point is optional. Create and edit answer `{ zone, message, messageCode }` (MSG-GEN-05).
- Licenses (FARM-03): a license belongs to one zone (`zoneId`; the older `zoneIds` is read for one release, with one
  zone only) and is added, edited or replaced only while that zone is In Use (MSG-GEN-09). The expiry date is required;
  the type, holder and legal scope are optional. A license is Replaced from the day the license replacing it is recorded,
  whatever its dates (BR-FARM-06); a Replaced license cannot be edited or replaced again (MSG-GEN-09).

### Notifications

Every notification a module sends is kept in the list of its recipient account, whatever the preferences and even when
it could not be pushed (BR-ACCOUNT-12). Each type of `NotificationTypes.All` has a push policy and the roles it is sent
to (BR-ACCOUNT-11); a module that sends a new type registers it there, and the dispatcher refuses unregistered types.

| Type | Content | Push | Roles |
|---|---|---|---|
| `WATER_ALERT` | MSG-WATER-03 | always (mandatory) | Owner, Zone Manager |
| `WATER_ALERT_WITHDRAWN` | MSG-WATER-04 | always (mandatory) | Owner, Zone Manager |
| `WATER_MEASUREMENT_OVERDUE` | MSG-WATER-06 | unless turned off | Zone Manager; Owner when the zone has none |
| `ZONE_LICENSE_EXPIRY` | MSG-FARM-01 / MSG-FARM-02 | always (mandatory) | Owner |
| `COOPERATIVE_CERTIFICATION_EXPIRY` | MSG-FARM-01 / MSG-FARM-02 | always (mandatory) | Owner |
| `STAFF_ACCOUNT_LOCK_REMINDER` | MSG-LABOR-02 (LABOR-01.3) | never: list only | Owner |
| `PURCHASE_RECEIPT_COMPLETED` | MSG-PURCHASE-01, when a receipt is completed (never for a draft), also when the Owner completed it | unless turned off | Owner |
| `PURCHASE_RECEIPT_CANCELLED_ADVANCE` | MSG-PURCHASE-08, when a cancelled receipt leaves the supplier debt below zero | unless turned off | Owner, Accountant |
| `SUPPLIER_TRANSACTION` | MSG-PURCHASE-04, for a supplier payment, money paid back or a cancelled one, also when the Owner did it | always (mandatory) | Owner |

- Preferences (ACCOUNT-07): `GET`/`PUT /api/me/notification-preferences` list the types of the roles the account
  holds with `push` on or off (`items: [{ type, push }]`). Mandatory types are shown as always pushed and cannot be
  turned off; list-only types are not offered. `pushAllowedOnDevice` is false when no device registered by a logged-in
  session allows notifications; turning push on then still saves the choice and answers MSG-ACCOUNT-21 in `notice`.
- List (ACCOUNT-08): `GET /api/me/notifications?unreadOnly=&page=&pageSize=` (newest first, `unreadCount` of the whole
  list), `POST /api/me/notifications/{id}/read`, `POST /api/me/notifications/read-all`. A row keeps its English
  `title`/`body` and the `messageCode` with `messageParams`, a JSON object of atomic values (never an English phrase) from
  which the apps build the text in Vietnamese or English; a push carries the same code and parameters. Dates are ISO
  dates, times ISO 8601 with the Vietnam offset:

  | Code | `messageParams` |
  |---|---|
  | MSG-WATER-03 | `corrected` (bool), `zone_name`, `raft_code` (null: no raft yet, "–"), `measurement_time`, `values_outside`: `[{ indicator: PH \| SALINITY, value, unit: "‰" \| null, direction: High \| Low, min, max }]` |
  | MSG-WATER-06 | `zone_name`, `last_date` |
  | MSG-FARM-01 / MSG-FARM-02 (`ZONE_LICENSE_EXPIRY`) | `document_type` (license type code, null when none), `document_number`, `zone_codes` (list holding the license's one zone), `expiry_date`, `days` (MSG-FARM-01 only) |
  | MSG-FARM-01 / MSG-FARM-02 (`COOPERATIVE_CERTIFICATION_EXPIRY`) | `certification_name`, `certification_number` (null when none), `expiry_date`, `days` (MSG-FARM-01 only) |
  | MSG-PURCHASE-01 | `user_name`, `receipt_code`, `supplier_name`, `line_count`, `total_amount` (whole VND: the sum of the lines, each rounded half up to the dong) |
  | MSG-PURCHASE-04 | `user_name`, `transaction_kind` (`Payment`, `PaidBack`, `PaymentCancelled`, `PaidBackCancelled`), `payment_code`, `amount`, `supplier_name`, `debt_amount` (the debt after it; below zero: paid in advance) |
  | MSG-PURCHASE-08 | `user_name`, `receipt_code`, `supplier_name`, `advance_amount` (VND paid in advance, positive) |
- `GET /api/me/notifications/{id}/open` checks the access again (GFR-02): 403 MSG-GEN-08 when the user may no longer
  open the record (e.g. a revoked zone–role pair), 404 MSG-ACCOUNT-22 when the record was removed. A push carries the
  notification id.
- The same table keeps SMS and Zalo OA sendings to traders (`channel`, phone, delivery status Sending / Sent / Not
  sent, provider message id or error code); they are never in a list. The response to a purchase request (SALE-03) uses
  them through `ITraderMessageSender`, whose only route is SMS (`Sms:Provider`, Fake or None) until the Zalo OA templates
  are approved; SALE-06 and TRACE-04 follow.
- Mandatory types also go out as one SMS (PM decision OMTS-216, 03/11): to the Owners among the recipients and, for the
  water alert and its withdrawal, to the Zone Managers of the zone among them. The rule is data on the type
  (`NotificationSmsRule`, `NotificationTypes.All`). The SMS shares the queue of the push: sent once, only after the save
  commits, never for a failed save, never retried, and not kept as a row. A failed or slow SMS changes nothing; a
  recipient without a verified phone number gets none and a log line. The text is the MSG text without Vietnamese marks,
  at most 160 GSM 7-bit characters; a money type (`MoneyToOwner`) carries only its name, document code and amount, so
  never a bank account number. Operational texts read as their fields do, digits included. At 160 the values, allowed
  ranges, time, expiry date and, for a new purchase request, kg and pickup date stay; in order the free text gives way to
  a short minimum (zone name or list, raft code or license type name, license number; note, customer, product), then the
  closing sentence, then the longer wording of the water alert, then the free text below its minimum. Logs hold the type
  and the account id only, never what a provider answered.

### Audit log

Every create, edit and status change writes one `AuditEvent` in the same save as the change (GBR-02, NFR-02): who acted
(an account, an employee of the employee app, or the system when neither is set), when, the action
(`FarmingZone.Updated`, `Account.StatusChanged`…), the record (type, id, code), the zone, the reason and the values
before and after. Fields that may hold a password, a code or a token are removed when the row is written and again
when it is shown. Events are never edited or deleted and no job cleans them up (kept at least 5 years).

Only the Cooperative Owner searches it (ACCOUNT-10, BR-ACCOUNT-13); every other caller gets 403 MSG-GEN-08:
- `GET /api/audit-events`: newest first, paged (`page`, `pageSize` up to 100). `from`/`to` are Vietnam dates, both
  included: the last 30 days by default, at most 5 years, else MSG-GEN-01. Filters: one person (`actorAccountId`,
  `actorEmployeeId` or `system=true`), `action` and `recordType` (exact values), `recordCode` (codes starting with it),
  `zoneId`. No match: 200 with an empty page and MSG-GEN-10 in `message`.
- `GET /api/audit-events/facets`: the actions, record types and people to offer in the filters.
- `GET /api/audit-events/{id}`: the event with `before` and `after` (JSON).
- `GET /api/audit-events/{id}/record`: the record of the event as it is now, or 404 MSG-ACCOUNT-22 when it no longer
  exists (its events stay in the log).

### Database (EF Core code-first)

Entities live in `OMTS.Domain`; the DbContext, configurations and migrations live in `OMTS.Infrastructure/Persistence`.
`dotnet ef` uses `OMTS.Api` as the startup project, so it reads the same `OmtsDb` user-secret as the API — set it once per machine (see above).

```bash
dotnet tool install --global dotnet-ef   # once
# create/update your local database from the migrations
dotnet ef database update --project src/OMTS.Infrastructure --startup-project src/OMTS.Api
# after changing entities or configurations
dotnet ef migrations add <Name> --project src/OMTS.Infrastructure --startup-project src/OMTS.Api --output-dir Persistence/Migrations
```

### External services (development)

Email, SMS, push, OCR and file storage are adapters picked by configuration, so switching provider needs no code change.
In Development the fakes keep what they "send" in memory; read it at `GET /api/dev/sms?phoneNumber=`,
`GET /api/dev/email?to=`, `GET /api/dev/push?accountId=` (clear with `DELETE`). Every push is kept, and its `devices`
lists the tokens a real provider would deliver to: those registered (`POST /api/me/devices`) by a session that is still
logged in. These endpoints answer 404 outside
Development; they are anonymous, so never expose a Development environment publicly. Local storage writes under `src/OMTS.Api/App_Data/files` (git-ignored); for Azurite set
`Storage:Provider=AzureBlob` and `Storage:AzureBlob:ConnectionString=UseDevelopmentStorage=true`.
The OCR Service of the water meter photos (WATER-01) is only faked until its provider ticket: the fake answers with
`Ocr:Fake` (default pH 7.8, salinity 25.5, temperature 28.5), unless the photo bytes contain a marker such as
`ph9.1_sal20_tmp28.5`, `conf0.6`, `at2026-10-20T08:00`, `ocr-fail` or `ocr-timeout` (used by the tests).
The Payment Notification Service (API-12) has no provider yet, so there is only an interface (`IPaymentNotificationService`)
and a fake that works in Development: it accepts every receiving bank account except the numbers of
`Payments:Fake:RejectAccounts` (default `REJECT000001`) and `Payments:Fake:TimeoutAccounts` (default `TIMEOUT00001`, never answers).

### Background jobs

Every daily job runs on one base (`DailyJobHostedService`): a run time in business time (UTC+7), an optional catch-up run
when the host starts after it, and a failed run logged without stopping the next day's.


`WaterMeasurementOverdueJob` runs every day at 06:00 (Asia/Ho_Chi_Minh), GFR-04: each In Use zone with a raft that has an
Open cycle counts from the later of its latest Active water measurement and its earliest Open cycle start. More than one
calendar month after that date it gets MSG-WATER-06, sent to the Zone Managers of the zone, or to the Owners when it has
none, and sent again a month after the last reminder while no new measurement moves the date. A void moves the date back;
the repeat still counts from the last reminder. Like the expiry reminder (07:00), a host started after the run time
runs it once at start: a host that was down across 06:00 sends the reminders when it next starts after 06:00. A second run
on the same day sends nothing. Each zone is saved on its own: a zone saved during the run, or one with nobody to notify,
is simply checked again the next day, and the other zones are not affected.

The expiry reminder (GFR-03, `ZoneLicenseReminderJob`, 07:00) checks the zone licenses and the cooperative certifications.
It notifies the active Cooperative Owners only (mandatory push), once when a record becomes Expiring soon (30 days or fewer,
MSG-FARM-01) and once when it becomes Expired (MSG-FARM-02); a record found already Expired gets the Expired notice only.
Every license of an In Use zone that is not Replaced is reminded on its own state, not only the newest one: a Replaced
license and the licenses of a Discontinued zone are not. After a resume, each of these licenses gets the notice of its
current state at the next check if that notice was not sent yet; nothing is sent twice. A new expiry date re-arms both
notices.

A replaced raft photo, license file, cooperative cover photo, certificate file or cultivation log photo is removed right after the save (a removed
certification, cover photo or cultivation log too), and the upload of a failed save right away. `OrphanFileCleanupJob` runs every day at
03:00 (Asia/Ho_Chi_Minh), with no catch-up at start unless `CatchUpOnStart` is set, and deletes what such a removal left behind: raft photos (`raft-photos/`), license files
(`zone-licenses/`), cover photos (`cooperative-covers/`), certificate files (`cooperative-certifications/`), cultivation log photos (`cultivation-log-photos/`) and water meter
photos (`water-meter-photos/`, a recognition that was never saved as a measurement) that no record points to and that were
written more than the grace period (24 hours) ago. No other storage folder is scanned, so cooperative logos are kept for ever.

**Every environment must use its own storage container (`Storage:AzureBlob:Container`) or folder
(`Storage:Local:RootPath`); never share one between environments.** The job deletes the files its own database does not
point to, so on a shared storage it would delete the files of the other environment. As a safeguard only, a raft photo or
license folder in which not one stored file (old or new) is referenced by the database is skipped: nothing is deleted
there and a warning is logged. Cover photos and certificate files may all be unreferenced in a real database (cover
removed, last certification removed), so their folders are skipped only when the database holds no cooperative. Every
cultivation log may be removed too, so its folder is skipped when the database holds no cooperative or has no
`CultivationLog` audit row: it never held a log, while those rows stay after the last log is removed.

## Deployment

There is no System Administrator inside the cooperative app: the cooperative and its first Cooperative Owner are
created by a one-time command run on the server by the deployment operator (not reachable over HTTP).

1. Set the secrets (environment variables or the hosting secret store; `__` replaces `:` in variable names):
   `ConnectionStrings:OmtsDb`, `Jwt:SigningKey`, the provider keys below, and the bootstrap keys
   `Bootstrap:Cooperative:FullName`, `Bootstrap:Owner:DisplayName`, `Bootstrap:Owner:Username`,
   `Bootstrap:Owner:PhoneNumber`, `Bootstrap:Owner:Email` (optional), `Bootstrap:Owner:TemporaryPassword`.
2. Run `dotnet OMTS.Api.dll seed --bootstrap` at handover time: it applies migrations, checks the status catalog, creates
   the rows of the two fixed roles (Zone Manager `ZONE_MANAGER`, Accountant `ACCOUNTANT`; their permissions are the
   static §I.4.4 matrix, never stored) and, if no cooperative exists, the cooperative (full name only) and the
   Owner with this first password. It must be changed at first login and does not expire. At that login the Owner first
   verifies the phone number by SMS (BR-ACCOUNT-03), so `Sms:Provider` must work before the handover. Running it again creates nothing that already exists ("already initialized", exit code 0); the roles are found by
   code. Missing secrets give exit code 1 and the
   names of the missing keys. Outside Development a database where any account holds the local demo password is refused
   (exit code 1, nothing written). `--demo` also seeds the demo data of "Run with Docker" (in Development, or in Staging as
   described in [Demo on a shared server](#demo-on-a-shared-server); refused in Production and any other environment).
   Upgrading a database from before the fixed roles (migration `FixedRoles`): the rows named "Zone Manager" and
   "Accountant" get the codes, other roles get a placeholder code (they are not listed, and their pairs give no
   permission, login or zone any more), and the validity dates of the pairs are dropped, so expired and future pairs and
   pairs of discontinued roles become Effective again; of two Effective copies of one pair only the newest stays. Down is
   lossy (no stored permissions, every role In Use). Demo and test databases are not converted further: reset them
   (`docker compose down -v`).
   Upgrading a database from before the purchasing catalogue of SRS 27/10 (migration `PurchaseCatalogueV2`): the item
   group `RaftMaterial` becomes `Material`, the supplier's free-text "other contacts" become its address, and the
   migration stops when two farming input items have the same name (rename one first, or reset a demo database).
   Upgrading a database from before the purchase receipts of SRS 27/10 (migration `PurchaseReceiptsV2`): receipts lose their
   zone, discount, paid-now amount and invoice photos; their lines get numbers and the receipt raft on their Seed and
   Material lines; seed lots become the Seed lines themselves (the seeding events are re-pointed); old codes
   (`PR-yyyyMMdd-nnn`) stay and new receipts take `PN000123` codes from the `DocumentSequences` counter, which starts after
   the highest PN code and never gives a number twice. Down is lossy. Demo databases are not converted further:
   reset them (`docker compose down -v`).
   Upgrading a database from before the supplier payments of SRS 27/10 (migration `SupplierPaymentsV2`): payments are no
   longer linked to receipts and lose their evidence files; the rows get kind `Payment` and `TT000123` codes in payment order,
   and the `DocumentSequences` counter starts after them. Down is lossy (money
   paid back is dropped, amounts are rounded to thousands). Demo databases are not converted further: reset them.
   Upgrading a database from before the purchase returns of SRS 27/10 (migration `PurchaseReturnsV2`): the rows become Recorded
   and keep their amounts and `PRT-…` codes, new returns take `TH000123` codes; the refund columns are dropped without turning
   them into money paid back, so a refund already on a return leaves the debt. Down is lossy (Cancelled returns are deleted, amounts
   are rounded to thousands). Demo databases are not converted further: reset them.
   An Owner who cannot log in (for example after losing the first password) is recovered with
   `dotnet OMTS.Api.dll seed --reset-owner-password`. It sets `Bootstrap:Owner:TemporaryPassword` (from configuration, never
   from the command line, so it never lands in shell history) as a new password that must be changed at first login and
   does not expire, ends every session of the Owner and writes an audit row. Set a new value first: the Owner's current
   password is refused. With several Owners, `Bootstrap:Owner:Username`
   chooses one. Without an Owner, or without a valid password, it changes nothing and exits with code 1.
3. Start the API and check `GET /health/ready`: 200 when the database, migrations, status catalog, the two fixed role
   rows (by code), cooperative and an active Owner are all in place, else 503 listing the failing checks. This is a
   post-deployment verification, **not** a platform probe: it depends on business data (locking the only Owner turns it
   503). Point App Service / Kubernetes readiness and load-balancer probes at `GET /health/db`
   (database reachable and migrated). `GET /health` is liveness only.

| Area | Keys |
|---|---|
| Email (`Email:Provider` = `Smtp` or `Fake`; `Email:Smtp:Security` = `StartTls` (587), `SslOnConnect` (465) or `None`, which is refused outside Development) | `Email:Smtp:Host`, `Port`, `Security`, `Username`, `Password`, `FromAddress`, `FromName`, `TimeoutSeconds`, `Email:Fake:FailFor` |
| SMS (`Sms:Provider` = `None`, the default: nothing is sent, phone verification cannot complete and each save with mandatory SMS writes one warning; `Fake` keeps messages in the dev inbox and is set in Development only) | `Sms:Fake:FailFor` (test numbers that fail), `Sms:MandatoryTimeoutSeconds` (default 5, from 1 to 60 or the host does not start: the longest the answer of an action waits for the SMS of its mandatory notifications) |
| Push (`Push:Provider` = `None` by default, `Fake` in Development) | `Push:Fake:FailFor` |
| OCR (`Ocr:Provider` unset by default: meter photos are not read and the values are entered by hand (MSG-WATER-01); `Fake` invents readings, so it is set in Development only and stops the host elsewhere) | `Ocr:TimeoutSeconds` (default 15), `Ocr:UncertainBelow` (default 0.8), `Ocr:Fake:Mode` (`Success`, `Fail`, `Timeout`), `Ocr:Fake:Ph`, `Ocr:Fake:Salinity`, `Ocr:Fake:Confidence` |
| Payment notifications, API-12 (`Payments:Provider` = `None`, the default: no provider is chosen, so no receiving bank account can be accepted and the Owner gets MSG-DEBT-02; `Fake` works in Development only and elsewhere acts as `None`) | `Payments:TimeoutSeconds` (default 15; 1 to 300, anything else stops the host at startup), `Payments:Fake:RejectAccounts`, `Payments:Fake:TimeoutAccounts` |
| File storage (`Storage:Provider` = `Local` or `AzureBlob`; `Storage:Local:SigningKey` is required outside Development; one container or folder per environment, see Background jobs) | `Storage:Local:RootPath`, `Storage:Local:SigningKey`, `Storage:AzureBlob:ConnectionString`, `Storage:AzureBlob:Container` |
| Bootstrap (see Deployment) | `Bootstrap:Cooperative:FullName`, `Bootstrap:Owner:*` |
| Rate limits per client IP (an IPv6 client per /64) and minute | `RateLimits:CodeRequestsPerMinute` (default 10: every endpoint that sends or checks a code), `RateLimits:LoginsPerMinute` (default 30) |
| CORS: the web app calls the API from the browser. Set its origin in every deployed environment; empty means no CORS. Development allows `http://localhost:3000`. | `Cors:AllowedOrigins` (array, e.g. `Cors__AllowedOrigins__0=https://<web host>`) |
| Reverse proxy: X-Forwarded-For is trusted only from these addresses (and loopback), so the limits see the real client | `ForwardedHeaders:KnownProxies` (e.g. `10.0.0.4`), `ForwardedHeaders:KnownNetworks` (e.g. `10.0.0.0/24`) |
| Verification codes: SMS sent system-wide per 24 hours; beyond it SMS codes answer MSG-ACCOUNT-13 and a critical log entry is written (BR-ACCOUNT-09: one purpose and one number or address get at most 3 codes per hour, at least 60 seconds apart; over all purposes one number or address gets at most 10) | `Verification:DailySmsLimit` (default 500) |
| Expiry reminder of zone licenses and certifications (GFR-03, daily, business time zone UTC+7) | `Jobs:ZoneLicenseReminder:RunAt` (default `07:00`), `Jobs:ZoneLicenseReminder:CatchUpOnStart` (default `true`: a host started after the run time runs once at start) |
| Overdue water measurement reminder (GFR-04, daily, business time zone UTC+7) | `Jobs:WaterMeasurementOverdue:RunAt` (default `06:00`), `Jobs:WaterMeasurementOverdue:CatchUpOnStart` (default `true`) |
| Employment end: an employee is Left from the day after the last working day (LABOR-01, daily, business time zone UTC+7) | `Jobs:EmploymentEnd:RunAt` (default `00:01`), `Jobs:EmploymentEnd:CatchUpOnStart` (default `true`) |
| Orphan file clean-up: raft photos, license files, cover photos, certificate files, cultivation log photos, meter photos (daily, UTC+7) | `Jobs:OrphanFileCleanup:Enabled` (default `true`), `RunAt` (default `03:00`), `GracePeriod` (default `1.00:00:00`, at least `01:00:00` or the host does not start) |

## Demo on a shared server

A server several people reach runs in **Staging**. **Never use `docker-compose.yml` as the shared server:** it runs in
Development and binds `0.0.0.0:8080`, so anyone on that network can read every verification code in the anonymous dev
inbox, open Swagger and log in with the password printed in this README.

`seed --bootstrap --demo` is decided before anything is written (`DemoSeedGate`), so run it first, on the empty database,
not after a plain `seed --bootstrap`:

| Environment | Demo data | Password of the demo accounts |
|---|---|---|
| Development | always | `Demo@1234`, as in "Run with Docker"; the two keys below are ignored |
| Staging | only with `Demo:AllowOutsideDevelopment=true` | the secret `Demo:Password` |
| Production, any other name | never, whatever the configuration says | – |

`Demo:Password` comes from the secret store of that server, never from a file in the repository. `appsettings.Staging.json`
and `appsettings.Production.json` are ignored by git and left out of the image for this reason; a settings file put beside
the program by hand is still read. The password is refused when it is empty, starts or ends with whitespace, is the
well-known password in any letter case, or breaks the password rule (8 to 128 characters with a letter and a digit). A
refusal gives exit code 1 and names the key, never the value.

To set up the server:
1. Set `ASPNETCORE_ENVIRONMENT=Staging` (and not a different `DOTNET_ENVIRONMENT`: when both are set and differ, the seed
   and the API refuse to start), the secrets of "Deployment" step 1 for an **empty** database, one storage folder or
   container of its own, `Demo__AllowOutsideDevelopment=true` and `Demo__Password`. Use fake data in `Bootstrap:*`.
2. Run `dotnet OMTS.Api.dll seed --bootstrap --demo`, start the API and check `GET /health/ready`.
3. Give the password to the testers privately. The nine logins of "Run with Docker" all use it, the Owner included, and
   land in the same branches. Put the server behind HTTPS: the image itself listens on plain HTTP.

**Which database.** The demo only runs on a database with no accounts, or on one it seeded itself: the first run writes a
`Demo.Seeded` audit row. Any other database with accounts, such as a copy of real data, one behind the migrations or a
mistaken connection string, is refused: exit code 1, nothing written, no migration applied. **Never run the demo seed on a
copy of real data: start from an empty database.** Refused too, by the demo seed and also by a plain `seed --bootstrap`
in any environment but Development: a database where any account holds `Demo@1234` (first seeded in Development, or
restored from such a copy). The message names the accounts, never a password. Starting the API does not check this, so a
copy restored onto the server and started without a seed still answers to the printed password.

**Reruns.** A rerun on the demo's own database adds only what is missing. It never touches the Owner's password, forced
change or verified phone, so `seed --reset-owner-password` stays as it was left, and the seed never changes a password it
has already set. So:
- A new `Demo:Password` changes no account: to change it, start again from an empty database.
- After a run that crashed half way, rerun with the same secret, or start from an empty database. A rerun with a rotated
  secret exits 0 and can leave two passwords: the accounts seeded before the crash stay on the old one.
- A tester who sets any account's password to `Demo@1234` makes the next seed refuse the whole server. The password rule
  does not forbid that value, and the printed password then logs in to that account: start again from an empty database.

**What stays off.** The dev inbox (`/api/dev/sms`, `/email`, `/push`) and Swagger answer 404 outside Development; no
setting opens them.

**SMS, email, push, OCR.** No real SMS or push provider exists yet, so a code cannot reach anyone on this server:
- The seeded accounts need no SMS: their phones are stored as verified. The exception is `nhanvien.chuaxacthuc`, which
  logs in with `phoneVerificationRequired = true`, gets MSG-ACCOUNT-04 on its business calls and cannot complete the step.
- Also out of reach: trader registration, password recovery by SMS, and the first login of any staff account created on
  the server (it must verify its phone). The Owner can still reset a staff password, and the forced password change of
  `nhanvien.doimk` works.
- Leave `Sms:Provider` and `Push:Provider` at `None` and `Ocr:Provider` unset. A code request then answers 503
  MSG-ACCOUNT-13, so nobody waits for a message that will never come.
- `Fake` is not refused for SMS, email or push: the host only logs a warning, the client is told the code was sent, and
  nobody can read it because the inbox answers 404. `Ocr:Provider=Fake` stops the host.
- `Email:Provider` defaults to `Smtp`. Without `Email:Smtp:Host` the host starts with no warning and each send fails
  (503). The demo accounts have no email address, so the demo does not need it.
- When a real SMS provider arrives, do not point it at this data as it is: the demo phone numbers (`0900 000 0xx`) are
  made up and may belong to someone.

**No real bank account or payment-service key.** This is a rule for the deployer, not a check: the code has no payment
service, so there is no key to configure, store or refuse. The seed reads only `Bootstrap:*` and `Demo:*`; the bank
numbers of its employees are placeholders in `DemoEmployeePlans`, and it stores no cooperative bank account. Never enter
a real bank account on this server, in an employee record or anywhere else. Whoever adds a payment provider also adds
the refusal to `DemoSeedGate`.

## Design documents

`docs/sds/` holds the detailed design (SDS) of the modules, written from this code. A PR that changes a
documented module updates its SDS part in the same PR.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) — branches, Jira keys, commit/PR rules, releases.
