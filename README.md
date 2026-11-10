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
| Redis (containers only) | Not published. Holds the revoked-session lookup, without persistence; stopping it only makes each request ask SQL Server (see [Session revocation store](#session-revocation-store-redis)) |

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
an expired license replaced by one expiring within 30 days; `DEMO-02`: a valid license and an older expired one), about 10 rafts with lanes per zone (the first five of each In Use zone with an Open cultivation cycle, the first three with cultivation logs by the zone manager), 4 job types with hourly rates (one with a rate still
to come, one Discontinued), two versions of the attendance settings, 9 employees (one Left, one without a phone number; the 3 seasonal workers have job types),
payroll headers of the last two months without lines (Finalized, Awaiting finalization: a date up to the end of last month answers `MSG-LABOR-04`),
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

Rafts `B1-01`, `B1-02`, `B2-01`, `B2-02` have material usage inside their Open cycles, and `B1-06`, `B2-06` a repair with no cycle, recorded by the zone's manager.
The first three rafts of each In Use zone have seeding sessions by the zone's manager: the first with a re-hung lane and its seeding marked complete (the Owner has a MSG-CYCLE-02), the second with a lane left Not seeded, the third with every lane seeded. The rafts `B1-04`, `B1-05`, `B2-04`, `B2-05` keep a cycle with no record, which can still be cancelled. Receipts with seeded lots can no longer be cancelled.
Rafts `B1-01` and `B2-01` have two harvest lots of the seed day (the second with `-2`), one lane Being harvested and one Harvested; `B1-02` has every seeded lane finished, so `quanlyvung1` has the reminder to close its cycle (MSG-CYCLE-08), and the Owner has a MSG-HARVEST-01 for each lot. Harvest settings are at their first-setup values, 50 kg per cage and 3 days. The demo lots are dated the seed day, so the daily close check leaves them for sale on the first start; three days later it warns the Zone Managers (MSG-HARVEST-05) and the day after it closes them (GFR-05).
`quanlyvung1` sells the first lot of `B1-01` in cash (`HD000001`) and ends the sale with the QR code: that lot is Published (TRACE-01) and the invoice has its QR link (sent to the fake inbox; with a real SMS sender the sale is left unfinished). `quanlyvung2` sells the first lot of `B2-01` part in cash, pay later (`HD000002`), left unfinished so that the end of the sale can be tried. Publish one lot from the Trace lot screen with `GET`/`POST /api/harvest-lots/{id}/publish-check|publish` (a Not published lot on an invoice; a Withdrawn lot, Owner only).

Dates are set relative to the day of the first seed and are not moved afterwards:
- After a few weeks the "expiring" license and certification have expired. `docker compose down -v` gives fresh dates.
- Receipt codes follow one sequence in the order they were seeded (`PN000001`…`PN000020`), not the receipt date, and so do
  payment codes (`TT000001`…) and return codes (`TH000001`…).
- The debt age and the current-month statement of a supplier move with the calendar; pass `from`/`to` to the statement.
- The demo has no Open cycle yet, so GFR-04 sends no reminder (MSG-WATER-06) in the demo until the cycle demo data arrives.
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

**Web app and Zalo Mini App.** In Development `Cors:AllowedOrigins` allows `http://localhost:3000` (the Next.js dev
server) and `https://h5.zdn.vn` (Zalo serves every `zmp deploy` version of the Mini App, Development, Testing and
published, from that origin). For another origin, add a line such as `Cors__AllowedOrigins__2: http://192.168.1.10:3000` under
`environment` in `docker-compose.yml`, then run `docker compose up -d`.

**Update, reset, stop.**
- `docker compose up -d --build`: after a pull. It rebuilds the image, and the seed applies new migrations. An `.env` made
  before Redis was added needs the line `REDIS_PASSWORD=` from `.env.example`, or compose stops with "set it in .env".
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

`POST /api/auth/login` (a phone number or a username, and the password) returns a 15-minute access token and a
refresh token (7 days web / 30 days mobile, rotated on every `POST /api/auth/refresh`); `POST /api/auth/logout`
revokes the session. Every `AuthTokens` response (login, refresh, `zalo/login`; also the `tokens` object of a password
change) has the field `refreshTokenRotated`. Normally it is `true`: `refreshToken` is a new token, and after a refresh
the old one stops working. If the client sends the token that its previous refresh just
replaced (the response was lost, or two requests carried the same token), it gets, for 30 seconds (`Jwt:RefreshGracePeriod`),
a new access token and the token it sent, echoed back with the session's current `refreshTokenExpiresAt`, and
`refreshTokenRotated` is `false`: no new refresh token is issued, the session is not rotated again, and the client keeps
the token it already holds (a web server must not write cookies for such a response). The session, the account status and
the effective grants are checked as for a rotation. Any other old token, or the replaced one after the grace period, is
replay and revokes the session. A refresh response that never arrives is not repaired: the client that kept the old token
ends the session when it uses it after the grace period and must sign in again. An access token never outlives its
session: its `exp` is the earlier of the 15-minute lifetime and the session's absolute expiry. Token validation accepts
30 seconds of clock skew, so a token can be accepted up to 30 seconds after its `exp`, and the session's absolute expiry
can be overrun by up to 30 seconds. Locking an account
revokes all its sessions in the same save, and a login that races the lock revokes the session it just stored.

**Login identifier.** A phone number or a username; an email address is never an identifier, so logging in with one fails
like any unknown identifier. A phone number logs in whether or not it is verified (the password proves who the person is);
an unverified phone then meets the phone-verification gate (`AccountStateGateFilter`, MSG-ACCOUNT-04): every business call
is refused until the phone is verified. Email is a contact for notifications only: it is no longer a way to register
(`POST /api/auth/register` and `POST /api/verifications` accept `channel` `Sms` only), to recover a password
(`POST /api/auth/recovery` sends an SMS code to a verified phone number; an email identifier gets the same answer and no code) or to
prove identity when changing the password (`POST /api/me/password` needs the current password; `POST /api/me/password/code` is gone).
Adding, changing and verifying an email in the profile still works. A trader account created through the old email registration
has no phone number: it cannot log in by phone, and a username (generated at registration) is not something the person knows. There is
no migration for such accounts; run `SELECT Id, Username, Email FROM Accounts WHERE PhoneNumber IS NULL AND Email IS NOT NULL` before deploying
to see whether any exist (staff and Owner accounts always have a phone number).

**Devices and sessions.** `POST /api/auth/login`, `zalo/login` and `zalo/link` accept an optional `deviceId`: a random id
the browser or app install keeps, base64url, at most 128 characters; missing, empty or `unknown` means no device. The server stores only
a keyed hash of it (`DeviceKey`, derived from the signing key), never the id, and never uses it to authenticate. Sessions
without a device are never grouped. A session also records how it was opened (`AuthMethod`: `Password` or `ZaloMiniApp`) and why it
ended (`RevokeCause`: `Revoked` or `TokenReuse`). Under `/api/me`, for the caller's own account only (another account's session answers 404):

| Request | Answer |
|---|---|
| `GET sessions` | Open sessions, newest first: `id, clientType, authMethod, deviceKey, deviceName, userAgent, maskedIpAddress` (`203.0.*.*`), `signedInAt, lastActiveAt, expiresAt, isCurrent` |
| `DELETE sessions/{id}` | 204, also when the session had already ended; the current session may be ended (the next request is 401) |
| `POST sessions/revoke-others` | 200 `{ "count": n }`: every open session except the current one |
| `GET devices` | Open sessions grouped by `deviceKey` (`deviceKey, clientType, authMethod, deviceName, userAgent, maskedIpAddress, firstSignedInAt, lastActiveAt, isCurrent, sessionIds`); a session without a device is its own entry with a null `deviceKey` |
| `DELETE devices/{deviceKey}` | 204 always (idempotent): ends the caller's sessions on that device. `deviceKey` is 43 base64url characters; the route shares its prefix with `DELETE devices/{token}` (push token), which keeps any other shape |

Revoking goes through the same path as logout, so the next request of an ended session is 401 (through Redis when configured). A
password change keeps the device and sign-in method of the session it replaces. `DELETE /api/me/zalo-link` ends every `ZaloMiniApp`
session of the account, on every device, and the current session.

**Refusing a refresh.** A refused `POST /api/auth/refresh` keeps its status and problem shape (401 MSG19; 403 when the account may no
longer log in) and adds the problem extension `reason`: `expired` (the lifetime ended, or the session no longer exists),
`reuse` (a replaced token was presented again, so the session is revoked) or `revoked` (a user or an administrator ended it: logout
elsewhere, a session or device revoked, a lock, a password change). A Mini App signs in again by itself after `expired` or `reuse` only.
`POST /api/auth/zalo/login` with a `deviceId` whose newest session of that account was revoked by a user or an administrator answers
403 `ZALO_DEVICE_REVOKED`; only `POST /api/auth/zalo/link`, which creates a newer session, lets the device in again.
A session revoked for reuse does not block silent login. Sessions are deleted 30 days after they expired or were revoked
(`Jobs:LoginSessionCleanup`), and from then on a device that was signed out is no longer remembered as such.

`GET /api/me` and `GET /api/me/permissions` return the caller and their effective
zone-scoped permissions. `GET /api/me` also carries `mustChangePassword` and `phoneVerificationRequired`, the same
account state the gate behind the 403 `PASSWORD_CHANGE_REQUIRED` / MSG-ACCOUNT-04 answers uses, and stays reachable while
either is true, so the web can route onboarding without waiting for a 403. Both can be true at once; the phone step
comes first. Errors are RFC 7807 problem details whose `title` and `code` are the message code of
the SRS 27/10/2026 catalogue (`MSG-GEN-01`, `MSG-ACCOUNT-05`…) and whose `detail` is the SRS English text. For one
release they also carry `legacyCode`, the old code of the same situation (`MSG01`, `MSG12`…), so web and mobile can
switch on their own schedule. A few old codes have no SRS 27/10 message yet and are still sent as `code` (MSG05,
MSG06, MSG19, MSG58, the technical codes `NOT_FOUND`, `UNEXPECTED_ERROR`…; full list in
`MessageCodes.Legacy.KeptAsCode`). Success responses that carry a text carry its code next to it (`messageCode`,
`noticeCode`).

Dates and times are ISO 8601. A time sent without a UTC offset (`2026-10-20T08:00:00`, in JSON or a multipart field)
is Vietnam time (UTC+7), never the server's time zone or UTC; `Z` or an offset such as `+07:00` is kept. Responses always
carry the offset. Business dates (`from`/`to` filters, receipt dates) are Vietnam dates.

#### Session revocation store (Redis)

Every authenticated request checks that its session is not revoked. With Redis configured, this is a lookup of
`omts:auth:rev:s:{sessionId}` instead of a database query. The database stays the source of truth:
- A saved revocation (logout, password change or reset, lock, refresh token reuse) is written to Redis right after the
  commit, with a time to live of the access token lifetime plus one minute (about 16 minutes). Keys hold session ids only.
- Every instance also copies the sessions revoked in that window from the database every 10 seconds
  (`Revocation:SweepInterval`) and then renews the marker `omts:auth:ready` (60 seconds). A write that failed after the
  commit is repaired by the next sweep, which is started at once; a revocation can therefore reach Redis up to about 10
  seconds late when Redis was unreachable at that moment.
- A request is answered from Redis only while the marker exists. When Redis is unreachable, slow (`Revocation:CommandTimeout`,
  250 ms; three failures within 5 seconds stop all Redis calls for 10 seconds), empty after a restart or flush, or the
  marker has expired, the request is checked against the database as before: a revoked session is still refused, a valid
  one still accepted. `/health/ready` lists `session-revocation` as Degraded (still 200) in that state; it is not part of
  `/health` or `/health/db`.
- **A lock performed outside the application** (a trader account locked by a direct database change, any other script)
  **must also set `RevokedAt` on that account's `LoginSessions`**. The Redis path no longer reads the account status, so
  without it the account keeps working for up to 15 minutes (until the access token expires; a refresh is refused at
  once). Locking in the application does both in one save.
- Revocations must be saved through EF Core entities. A bulk `ExecuteUpdate` on `LoginSessions` bypasses the write after
  commit and would be seen only by the next sweep.

Configuration: `ConnectionStrings:Redis` (a secret, e.g. `redis:6379,password=...`; empty by default), `Revocation:Mode`
(`Auto`, the default: Redis when the connection string is set, otherwise the database; `Redis`: the host does not
start without the connection string; `Sql`: the database always), `Revocation:CommandTimeout`, `Revocation:SweepInterval`
(1 to 30 seconds). Without a connection string nothing changes compared with before. Run Redis without persistence and
without eviction (`maxmemory-policy noeviction`): evicting a key would silently drop a revocation until the next sweep
notices, and a restart is harmless because the sweep refills it. `docker-compose.yml` does this.

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
| `CULTIVATION_LOG_RECORDED` | MSG-CYCLE-04, when a cultivation log is saved, not on a correction, not to the Owner who recorded it | unless turned off | Owner |
| `CYCLE_SEEDING_COMPLETED` | MSG-CYCLE-02, when the seeding of a raft is marked complete (once per cycle), not to the Owner who marked it | unless turned off | Owner |
| `HARVEST_LOT_RECORDED` | MSG-HARVEST-01, when a harvest lot is recorded, not on a correction, not to the Owner who recorded it | unless turned off | Owner |
| `HARVEST_LOT_AUTO_CLOSE_WARNING` | MSG-HARVEST-05, from the daily close check (GFR-05), once for each close date of a lot still for sale that the next check closes: to the Zone Managers of the zone (All zones too), or to the Owners when it has none | unless turned off | Owner, Zone Manager |
| `CYCLE_CLOSE_REMINDER` | MSG-CYCLE-08, once per cycle, when a harvest makes every seeded lane Harvested: to the Zone Managers of the zone (All zones too), or to the Owners when it has none, not to the Owner who recorded it | always (mandatory) | Owner, Zone Manager |
| `PURCHASE_RECEIPT_COMPLETED` | MSG-PURCHASE-01, when a receipt is completed (never for a draft), also when the Owner completed it | unless turned off | Owner |
| `PURCHASE_RECEIPT_CANCELLED_ADVANCE` | MSG-PURCHASE-08, when a cancelled receipt leaves the supplier debt below zero | unless turned off | Owner, Accountant |
| `SUPPLIER_TRANSACTION` | MSG-PURCHASE-04, for a supplier payment, money paid back or a cancelled one, also when the Owner did it | always (mandatory) | Owner |
| `CUSTOMER_PAYMENT_TRANSACTION` | MSG-DEBT-03, for a customer payment, money paid back or a cancellation saved in DEBT-02, and one notice per list of confirmations (no related record when the list holds several payments), also when the Owner did it | always (mandatory) | Owner |
| `PROVENANCE_WITHDRAWN` | MSG-TRACE-05, when the Owner withdraws the origin information of a lot the trader bought (TRACE-04); the row opens nothing | unless turned off | Trader |
| `TRADER_ACCESS_REQUESTED` | a trader asked for an account from the Zalo Mini App (`POST /api/auth/zalo/trader-requests`) | unless turned off | Owner |
| `TRADER_ACCESS_APPROVED` | the Owner approved the request (`POST /api/trader-requests/{id}/approve`) | unless turned off | Trader |
| `QUALITY_CLAIM_NEW` | MSG-CLAIM-01, a new quality claim (CLAIM-01) | unless turned off | Owner |
| `QUALITY_CLAIM_RESPONSE` | MSG-CLAIM-02, the Owner's response (CLAIM-02); also sent as an SMS to the phone number of the profile | unless turned off | Trader |

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
  | MSG-CYCLE-02 | `raft_code`, `seeded_count`, `lane_count` (lanes In Use) |
  | MSG-CYCLE-04 | `raft_code`, `visit_date` |
  | MSG-HARVEST-05 | `lot_code`, `raft_code`, `close_date` (the day of the check that closes the lot, at its start) |
  | MSG-PURCHASE-01 | `user_name`, `receipt_code`, `supplier_name`, `line_count`, `total_amount` (whole VND: the sum of the lines, each rounded half up to the dong) |
  | MSG-PURCHASE-04 | `user_name`, `transaction_kind` (`Payment`, `PaidBack`, `PaymentCancelled`, `PaidBackCancelled`), `payment_code`, `amount`, `supplier_name`, `debt_amount` (the debt after it; below zero: paid in advance) |
  | MSG-PURCHASE-08 | `user_name`, `receipt_code`, `supplier_name`, `advance_amount` (VND paid in advance, positive) |
  | MSG-DEBT-03 | one transaction: `user_name`, `transaction_kind` (`Payment`, `PaidBack`, `PaymentCancelled`, `PaidBackCancelled`), `payment_code`, `amount`, `customer_name` (null: no customer profile), `debt_amount` (the debt after it; below zero: money owed back; null without a profile); a list of confirmations: `user_name`, `transaction_kind` = `Confirmations`, `confirmed_codes` (the first 10), `confirmed_count`, `confirmed_more_count`, `confirmed_amount`, `cancelled_codes` (the first 10), `cancelled_count`, `cancelled_more_count`, `cancelled_amount`, `amount` (both together), `debts`: `[{ customer_name, debt_amount }]` after the cancellations (the first 5), `debts_more_count`; the text names the same ("PT000001, …, PT000010 and 189 more"), so a list of 200 fits the notification row |
  | MSG-TRACE-05 | `cooperative_short_name`, `lot_code`, `invoice_codes` (list), `cooperative_phone` (null: none set up yet, the text says "the cooperative") |
- `GET /api/me/notifications/{id}/open` checks the access again (GFR-02): 403 MSG-GEN-08 when the user may no longer
  open the record (e.g. a revoked zone–role pair), 404 MSG-ACCOUNT-22 when the record was removed. A push carries the
  notification id.
- The same table keeps SMS and Zalo OA sendings to traders (`channel`, phone, delivery status Sending / Sent / Not
  sent, provider message id or error code); they are never in a list. The response to a purchase request (SALE-03) uses
  them through `ITraderMessageSender`, whose only route is SMS (`Sms:Provider`, Fake or None) until the Zalo OA templates
  are approved. The sales invoice (SALE-06, MSG-SALE-15) uses them too: `POST /api/sales-invoices/{id}/finish`
  (`{ wantsQrCode }`, once, after the save: with a Yes, TRACE-01 first publishes the lots that are Not published and have all
  their origin information, `GET /api/sales-invoices/{id}/publish-check` shows them beforehand; the QR code only when a lot is
  Published; called again it answers the current
  result and sends nothing) and `POST /api/sales-invoices/{id}/resend` (only after `finish`, not while the latest sending
  is Sending; a Sending row older than 2 minutes reads Not sent); a profile without a trader account gets a row with no
  recipient, and a walk-in customer or a profile without a phone gets a Not sent row with no phone (error code
  `NO_CUSTOMER_PROFILE` / `NO_PHONE_NUMBER`, MSG-SALE-16). The withdrawal of a lot's origin information (TRACE-04, Owner
  only: `GET /api/harvest-lots/{id}/withdraw-preview`, `POST /api/harvest-lots/{id}/withdraw` with `{ reason }`) uses them
  too: one MSG-TRACE-05 per trader of the current invoices of the lot, listing their invoices, kept with the lot as its
  record; a profile without a phone gets a Not sent row (`NO_PHONE_NUMBER`), a walk-in invoice nothing. The notices are
  sent at the same time after the save (the answer waits about one send). The answer lists each notice with its delivery
  status; a failed send undoes nothing and is marked `toCall`; `GET /api/harvest-lots/{id}/withdrawal-notices` reads the
  notices of the latest withdrawal again. There is no resend. Texts and parameters longer than their columns (2000) are
  cut / left out, as for list rows.
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

### Public provenance page

`GET /api/public/provenance/{qrToken}?lang=vi|en` (ORIGIN-01, SCR-ORIGIN-01) is what the web page `/origin/{qrToken}` of the
invoice QR code loads. It needs no login and a logged-in user gets the same answer (BR-ORIGIN-01); it is limited per client IP
(`RateLimits:ProvenancePagesPerMinute`, 429 `TOO_MANY_REQUESTS`) and sent with `Cache-Control: no-store`, so each opening shows the
current data (BR-TRACE-05: a TRACE-03 correction or a new publish shows at once). The token is the 128-bit random base64url code
SALE-05 gave the invoice (22 characters); only the exact code opens the page.

- `lang`: `vi` by default (BR-ORIGIN-02, anything but `en` is Vietnamese). The messages (`message`) and the cooperative
  introduction follow it (the Vietnamese introduction when the English one is empty); every message also carries `messageCode` and
  `messageParams` (atomic values: `cooperative_short_name`, `lot_code`, `withdrawal_date`, `cooperative_phone`). The Vietnamese
  MSG-ORIGIN texts are proposals until the PM confirms them (the SRS gives English only).
- 200 `{ language, status, message, messageCode, messageParams, invoice: { code, date } | null, cooperative, lots[] }`:
  - `status` `Ok`: `lots` holds the lots of the invoice that are Published or Withdrawn, in line order, each `{ lotCode, products
    (names sold from the lot on this invoice), originState, withdrawalDate, message, messageCode, messageParams, origin }`. A
    Published lot has `origin { harvestDate, raftCode, zoneName, map { latitude, longitude, source: Raft | Zone } | null,
    seedings[{ seedingDate, seedItemName }], daysOfFarming, harvestPhotoUrls[], raftPhotoUrls[] }` (signed URLs, 1 hour); a
    Withdrawn lot has `withdrawalDate` and MSG-ORIGIN-01, never the reason, and `origin: null`. Not published lots are not listed.
  - `NoLots`: no lot is Published or Withdrawn, MSG-ORIGIN-02. `Cancelled`: the cooperative block only (`invoice: null`), MSG-ORIGIN-04.
  - `cooperative` is the FARM-01 public block (BR-FARM-09): `fullName, shortName, yearEstablished, officeAddress, phoneNumber, email,
    website, socialLink, logoUrl, coverPhotoUrl, introduction, certifications[{ name, number, issuer, expiryDate, state, fileUrl }]`
    (not Expired).
- 404 MSG-ORIGIN-03 (in `lang`) for a code no invoice has, also for a link that is no code at all.
- Never in the answer (BR-ORIGIN-04, NFR-03; a test fixes the exact set of properties): the customer, prices, amounts, payments,
  debts, any kg, returns, claims, suppliers, seed lot or receipt codes, the tax code, the bank account, the withdrawal reason or any
  internal id. A lot whose returned oysters were thrown away (CLAIM-03) will be left out once sales returns exist.

### Sales invoices

SALE-09 (`SALES_INVOICES`, zones of the caller's pairs, BR-SALE-08): the Owner, the Zone Manager of the zone and the Accountant view; the Zone Manager and the Owner also print. Any other zone answers 403 MSG-GEN-08.

- `GET /api/sales-invoices`: newest first, paged (`page`, `pageSize` up to 100). Filters: `from`/`to` (Vietnam dates, both included; today when neither is given, only `to` is that one day, only `from` runs to today, at most 5 years, else MSG-GEN-01), `zoneId`, `customerId`, `harvestLotId`, `raftId` (a line of a lot of the raft), `statusId`, `code` (part of the code, `%`, `_`, `[` are plain text), `paid` (true/false; a Cancelled invoice matches neither) and `cashCollectedByMe=true` (cash this caller collected that is Awaiting confirmation; each row then has `cashAwaitingConfirmation`). No match: 200 with an empty page and MSG-GEN-10 in `message`. A row has `code`, `invoicedAt`, `zone`, `customer` (null for a walk-in), `total`, `status`, `paymentTerm`, `isPaid` (null for a Cancelled invoice), `unpaidAmount` and `deliveryStatus`. `completedTotal` is the total of every Completed match, not of the page; `isPaid` comes from the debt allocation (BR-DEBT-05), a walk-in invoice from its own payments.
- `GET /api/sales-invoices/{id}`: the invoice with `lines`, `payments`, `sendings`, `priceCuts`, `debtEffect` (`isPaid`, `covered`, `unpaid`, `countsInCustomerDebt`, `customerDebt`) and `allowedActions` (SALE-09.1 step 3). The same fields are in the answers of `POST` create, `finish` and `resend`.
- `GET /api/sales-invoices/export` (the filters above, every page) and `GET /api/sales-invoices/{id}/print`: an `.xlsx` file for download and print, Export right in the zone of the invoice (the Accountant only views, §I.4.4 R: 403). Text is never a formula. An empty list is a header and a zero total.

`allowedActions`, on the rules of the SRS:

| Value | Offered to | When | Endpoint today |
|---|---|---|---|
| `Resend` | Zone Manager of the zone, Owner | not Cancelled, the sale is finished, the latest sending is not Sending | `POST /api/sales-invoices/{id}/resend` |
| `CreateReturn` | Zone Manager of the zone, Owner | not Cancelled, no sales return that is not cancelled | none yet: CLAIM-03 |
| `CancelReturn` | Owner | a sales return that is not cancelled (none can exist yet) | none yet: CLAIM-03.3 |
| `Correct` | Owner | Completed, no sales return that is not cancelled | `PUT /api/sales-invoices/{id}/correction` |
| `Cancel` | Owner | Completed, no sales return that is not cancelled | `POST /api/sales-invoices/{id}/cancel` |
| `Print` | Whoever has Export in the zone | always | `GET /api/sales-invoices/{id}/print` |

The endpoints refuse exactly what this table does not offer (`SalesInvoiceAllowedActions.ChangeRefusal`): 403 MSG-GEN-08, 409 MSG-GEN-09 for a Cancelled invoice, 409 MSG-SALE-20 with a sales return. The detail also has `corrections` (kind, date, reason, total before and after, from the audit log) and `lastCorrectionKind`, `lastCorrectedAt`, `lastCorrectionReason`; a Cancelled invoice has `cancelledAt` and `cancelReason`. The statement of a customer shows the corrections, price cuts and cancellation under each invoice (BR-DEBT-04).

SALE-07 `PUT /api/sales-invoices/{id}/correction` and SALE-08 `POST /api/sales-invoices/{id}/cancel` (Owner only, `reason` and the `rowVersion` read are required; a stale version answers 409 MSG06, so two parallel changes apply once). Questions are answered by sending again the field named in `confirmationField`.

- Correction `kind` `DataEntryError`: `lines` (all of them; unit price left out keeps the list price), `notSoldWholeLotIds`, `discount`, `otherCharges` (left out, kept) and `customerId` (another profile; the payments that name the invoice move with it and a linked request is unlinked). A lot added must be for sale (MSG-GEN-09), a lot already on the invoice may be Closed; a product already on the invoice may be discontinued. Only a changed mark acts on a lot: added closes it if still for sale, removed reopens it only when this invoice closed it (`ClosedBySalesInvoiceId`).
- Correction `kind` `PriceCutAfterClaim`: `priceCutAmount` above 0 and at most the total; the lines stay, and price cuts add up (AC-08). A walk-in invoice given money back needs `customerId` (MSG-SALE-19); a price cut takes `customerId` only then, otherwise MSG-GEN-01.
- A walk-in invoice cannot have its total raised (MSG-SALE-21), also when the same correction attaches a profile. A debt below zero after the change answers MSG-SALE-17 until `confirmOwedBack` is sent, then notifies the Owners and the Accountants (MSG-SALE-18, type `CUSTOMER_MONEY_OWED_BACK`).
- The correction is sent again after its commit (`(corrected)`); before the end of the sale nothing is sent, the end of the sale does.
- Cancel: each cash payment Awaiting confirmation is asked (MSG-SALE-23, `cashGivenBack: [{paymentId, givenBack}]`); only a Yes cancels it. A walk-in invoice with payments kept needs `customerId` (MSG-SALE-19). Only the lots this invoice closed reopen; a request it took in full is no longer Fulfilled, and every figure ignores it from its status. The trader gets MSG-SALE-22 (type `SALES_INVOICE_CANCELLED_MESSAGE`, not a sending of the invoice message) when the profile has a phone; failing to send never undoes the save (GFR-06).

Migration `20261109053538_SalesInvoiceCorrectionAndCancellation` only adds nullable columns.

### Quality claims (CLAIM-01, CLAIM-02)

A trader reports a quality problem on one of their invoices with photos; the Cooperative Owner responds. A claim changes neither the invoice nor the customer debt (BR-CLAIM-01/02).

- `POST /api/me/sales-invoices/{id}/quality-claims` (trader, multipart: `description` up to 2000 characters, one `photos` part per photo, 1 to 10 JPEG or PNG of at most 10 MB; 201 with the claim, code `KN000123`, and MSG-GEN-05). Only an invoice of the trader's own profile (a profile merged into theirs counts; else 404) that is not cancelled, has no sales return (sales returns come with CLAIM-03: the check is the seam `IInvoiceSalesReturnCheck`) and has no other claim Awaiting response (409 MSG-GEN-09, one filtered unique index per invoice). A photo that cannot be uploaded answers 502 MSG-GEN-03 and saves nothing. The Owner gets MSG-CLAIM-01 (not a mandatory type). Answered claims do not stop a new one and OMTS sets no time limit.
- `GET /api/me/quality-claims` (own claims, newest first, MSG-GEN-11 when none) and `GET /api/me/quality-claims/{id}` (404 for another trader's): the description, photo links, status, offer and response note. The photos are private (BR-CLAIM-01): signed links for 1 hour, never on the provenance page.
- `GET /api/quality-claims` (`statusId`, `page`, `pageSize`; Awaiting response first) and `GET /api/quality-claims/{id}` (customer with phone number, invoice with lines, lots, kg, amounts, status and price cuts, the SMS sendings, `rowVersion`): QUALITY_CLAIMS View, so the Owner and the Zone Managers, who see the claims of invoices in their zones only (PM default; another zone answers 403). An Accountant has no access.
- `POST /api/quality-claims/{id}/accept` (`{ offer: "ReturnAllGoods" | "PriceCutWholeInvoice", note?, rowVersion }`) and `POST /api/quality-claims/{id}/reject` (`{ note, rowVersion }`): Owner only, while Awaiting response; a response cannot be changed (409 MSG-GEN-09, or MSG06 when the form is stale). Accepting answers 409 MSG-CLAIM-03 when the invoice was cancelled or has a sales return (it can still be rejected). The offer only records what was proposed: the return is CLAIM-03 and the price cut SALE-07 (`PUT /api/sales-invoices/{id}/correction`, PriceCutAfterClaim), which the Owner runs after the trader chooses. Neither changes here. The trader gets a push and an SMS with MSG-CLAIM-02; a message that cannot be sent undoes nothing (GFR-06), it stays in the trader's list and the sending is listed Not sent on the claim; a profile without a phone number gets a Not sent row (`NO_PHONE_NUMBER`).

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
The incoming notifications (API-13) go through `IBankNotificationParser`, the provider's adapter, which checks the signature and
reads the transfers; the fake one reads the generic format below. `POST /api/dev/payment-notifications`
(`{ invoiceCode, amount, bankReference?, content?, accountNumber?, occurredAt?, outgoing? }`, anonymous, 404 outside Development)
fires a transfer to the receiving bank account through the same rules, so a VietQR sale can be finished without a bank; send the
same `bankReference` again to replay it.

### VietQR sale and bank notifications (SALE-05 steps 7-8, GFR-08, API-13)

- `POST /api/sales-invoices` with `payment.rest = "VietQr"` (all of the total, or the rest after `cashAmount`; a walk-in customer
  too) answers **202** with the sale awaiting its transfer: `{ code, status: "Awaiting", zone, customer, walkInCustomer, total,
  cashAmount, toPayAmount, receivedAmount, vietQr: { payload, bankBin, bankName, accountNumber, accountHolderName, amount,
  content }, createdAt, createdBy, invoiceId, invoice, endedAt }`. The code (HD…) is given now and kept (BR-SALE-08); nothing is
  saved yet: no invoice, payment, debt or lot close. `payload` is the EMVCo text the client draws as the QR image, made again on
  every read for the receiving bank account set at that time. Without a receiving bank account: 409 MSG-SALE-13. The questions
  (MSG-SALE-10/11/12) and `deduplicationKey` work as for any sale (the same key answers the same 202, or the 201 once saved).
- `GET /api/sales-invoices/pending/{code}` is the poll of the port (every 2–3 s): `Awaiting`, then `Completed` with `invoice`
  (the saved invoice and MSG-SALE-06, e.g. "Invoice HD000123 is completed: 8,000,000 VND received by VietQR; 4,000,000 VND
  collected in cash."); the end of the sale then goes on with `POST /api/sales-invoices/{id}/finish`. `GET
  /api/sales-invoices/pending` lists the sales still Awaiting in the caller's zones (an app that was closed finds its sale again).
- `POST /api/sales-invoices/pending/{code}/change-payment` `{ payment }` (Cash, part in cash with Pay later, or Pay later; not
  VietQR) saves the invoice under the same code as a new sale at the port (201, same questions); status `PaymentChanged`.
- `POST /api/sales-invoices/pending/{code}/stop` `{ confirmed }`: without `confirmed: true`, 409 MSG-SALE-14 (`confirmationField`
  = `confirmed`); with it, status `Stopped`, nothing saved but the audit row `SalesInvoice.SaleStopped` (record code = the code,
  reason "Stop sale").
- These need SALES_INVOICES Create in the zone of the sale (the Zone Managers of the zone and the Owner; others 403). A sale that
  already ended answers 409 MSG-GEN-09 with `status` and `invoiceId`. A pending sale has no time limit. Its harvest lots cannot be
  removed meanwhile (HARVEST-01.1: 409 MSG-GEN-09 "… is on a sale awaiting its transfer"), and the daily close (GFR-05) leaves its kg out of
  the leftover kg it records, because the transfer invoices them on the closed lot.
- `POST /api/integrations/payment-notifications` (API-13) is anonymous and rate limited (`RateLimits:PaymentNotificationsPerMinute`,
  256 KB): a notification the adapter does not find authentic answers 401. The fake adapter's format is
  `{ "transactions": [ { "bankReference", "accountNumber", "amount", "content", "occurredAt", "direction": "In" | "Out" } ] }`,
  signed as `X-Omts-Signature: sha256=<hex of HMAC-SHA256(raw body, Payments:Fake:WebhookSecret)>`. 200 lists for each transfer
  `{ bankReference, outcome, paymentCode, invoiceCode, reason }`; 400 for a body it cannot read; 5xx when the save failed, so the
  service retries. Each transfer is recorded once per bank reference (unique index on the reference of VietQR payments, so also
  when the service reports it twice at once), and not at all when the Accountant already recorded a payment with that reference
  that is not cancelled (the reference trimmed, as MSG-DEBT-05 compares it): `AlreadyRecorded` names the payment already recorded.
  A transfer time before 2000 or more than a day ahead is `Invalid`. A sale that can no longer be saved when its transfer arrives
  (a record it names is gone) never fails the notification: it is stopped by the system (audit `SalesInvoice.SaleStopped`, the
  reason says why) and the transfer is kept `WithoutCustomer`.
- What a transfer becomes (GFR-08), a customer payment with the method VietQR code of the invoice, Awaiting confirmation,
  recorded by the system (audit actor: the system), with the amount, bank reference and time reported:
  - `Ignored` (nothing recorded): `Outgoing`, `NotTheReceivingAccount` (another account, or the account before a change,
    BR-DEBT-01), `NoInvoiceCode` (no `HD` + 6 to 12 digits in the content, not glued to letters before it; case and a separator after HD are tolerated),
    `Invalid`.
  - `PaidInvoice`: a saved invoice has the code (a later payment of a Pay later invoice, a sale whose payment method was changed,
    a cancelled invoice): a payment of its customer (none for a walk-in invoice) that covers it first.
  - `CompletedSale`: the sale awaiting it, when the amount is at least `toPayAmount`, is saved Completed, Pay now, at the prices
    shown, with the transfer (its whole amount) and the cash part as its two payments, the lots closed (one closed meanwhile stays
    as it is) and MSG-SALE-08 to the Owners; the invoice and its cash are the Zone Manager's (creator, collector, audit actor).
  - `WithoutCustomer`: no saved invoice has the code (Stop sale, an unknown code, or less than the sale awaits, which then keeps
    waiting and shows it in `receivedAmount`): no customer and no invoice, the code kept in `transferInvoiceCode` of the
    payment, listed Awaiting confirmation for the Accountant (DEBT-02.6). Until DEBT-02.6 (settle: attach to a profile, or pay it
    back) ships in PR 7b, such a payment cannot be confirmed (409 MSG-GEN-09 "Customer payment PT… is without a customer profile …",
    `needsSettlement: true`), so it stays in the list; it can be cancelled.

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

`HarvestLotAutoCloseJob` runs every day at 00:05 (Asia/Ho_Chi_Minh), GFR-05: a harvest lot still for sale (Open or Reopened)
counts its days before automatic close (harvest settings) from its harvest date, or from the day it was last reopened. When
that day plus the days is before today, the lot is Closed with today as the close date, the reason `TimeLimitReached` and its
remaining kg as the leftover kg, and the audit row has the system as the actor: with 3 days, a lot harvested on 26/10 is
closed by the check of 30/10. The check of the day before (29/10) sends MSG-HARVEST-05 once, and the lot keeps the close date
it announced (`AutoCloseWarnedFor`), so a second run, the catch-up at start or a second app instance sends nothing more; a
reopen or new days give a new close date, warned once in its turn. A host down across the checks closes the overdue lots at
its next run (catch-up at start, like the other reminders), with that day as the close date. Each lot is saved on its own: a
lot saved meanwhile (a sale, a close by hand, the other instance) is read and checked once more at once.

The expiry reminder (GFR-03, `ZoneLicenseReminderJob`, 07:00) checks the zone licenses and the cooperative certifications.
It notifies the active Cooperative Owners only (mandatory push), once when a record becomes Expiring soon (30 days or fewer,
MSG-FARM-01) and once when it becomes Expired (MSG-FARM-02); a record found already Expired gets the Expired notice only.
Every license of an In Use zone that is not Replaced is reminded on its own state, not only the newest one: a Replaced
license and the licenses of a Discontinued zone are not. After a resume, each of these licenses gets the notice of its
current state at the next check if that notice was not sent yet; nothing is sent twice. A new expiry date re-arms both
notices.

A replaced raft photo, license file, cooperative cover photo, certificate file, cultivation log photo or harvest lot photo is removed right after the save (a removed
certification, cover photo or cultivation log too), and the upload of a failed save right away. `OrphanFileCleanupJob` runs every day at
03:00 (Asia/Ho_Chi_Minh), with no catch-up at start unless `CatchUpOnStart` is set, and deletes what such a removal left behind: raft photos (`raft-photos/`), license files
(`zone-licenses/`), cover photos (`cooperative-covers/`), certificate files (`cooperative-certifications/`), cultivation log photos (`cultivation-log-photos/`), harvest lot photos
(`harvest-lot-photos/`), quality claim photos (`claim-photos/`, a claim whose save failed after its uploads) and water meter photos (`water-meter-photos/`, a recognition that was never saved as a measurement) that no record points to and that were
written more than the grace period (24 hours) ago. No other storage folder is scanned, so cooperative logos are kept for ever.

**Every environment must use its own storage container (`Storage:AzureBlob:Container`) or folder
(`Storage:Local:RootPath`); never share one between environments.** The job deletes the files its own database does not
point to, so on a shared storage it would delete the files of the other environment. As a safeguard only, a raft photo or
license folder in which not one stored file (old or new) is referenced by the database is skipped: nothing is deleted
there and a warning is logged. Cover photos and certificate files may all be unreferenced in a real database (cover
removed, last certification removed), so their folders are skipped only when the database holds no cooperative. Every
cultivation log may be removed too, so its folder is skipped when the database holds no cooperative or has no
`CultivationLog` audit row: it never held a log, while those rows stay after the last log is removed. Harvest lot photos follow the same rule, with the `HarvestLot` audit row.

## Deployment

There is no System Administrator inside the cooperative app: the cooperative and its first Cooperative Owner are
created by a one-time command run on the server by the deployment operator (not reachable over HTTP).

1. Set the secrets (environment variables or the hosting secret store; `__` replaces `:` in variable names):
   `ConnectionStrings:OmtsDb`, `Jwt:SigningKey`, `Provenance:PublicBaseUrl` (the absolute URL of the web app; the API does
   not start without it), the provider keys below, and the bootstrap keys
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
   (database reachable and migrated). `GET /health` is liveness only. When Redis is configured but unreachable or empty,
   `/health/ready` still answers 200 and lists `session-revocation` as Degraded.

| Area | Keys |
|---|---|
| Email (`Email:Provider` = `Smtp` or `Fake`; `Email:Smtp:Security` = `StartTls` (587), `SslOnConnect` (465) or `None`, which is refused outside Development) | `Email:Smtp:Host`, `Port`, `Security`, `Username`, `Password`, `FromAddress`, `FromName`, `TimeoutSeconds`, `Email:Fake:FailFor` |
| SMS (`Sms:Provider` = `None`, the default: nothing is sent, phone verification cannot complete and each save with mandatory SMS writes one warning; `Fake` keeps messages in the dev inbox and is set in Development only) | `Sms:Fake:FailFor` (test numbers that fail), `Sms:MandatoryTimeoutSeconds` (default 5, from 1 to 60 or the host does not start: the longest the answer of an action waits for the SMS of its mandatory notifications) |
| Push (`Push:Provider` = `None` by default, `Fake` in Development) | `Push:Fake:FailFor` |
| OCR (`Ocr:Provider` unset by default: meter photos are not read and the values are entered by hand (MSG-WATER-01); `Fake` invents readings, so it is set in Development only and stops the host elsewhere) | `Ocr:TimeoutSeconds` (default 15), `Ocr:UncertainBelow` (default 0.8), `Ocr:Fake:Mode` (`Success`, `Fail`, `Timeout`), `Ocr:Fake:Ph`, `Ocr:Fake:Salinity`, `Ocr:Fake:Confidence` |
| Payment notifications, API-12 and API-13 (`Payments:Provider` = `None`, the default: no provider is chosen, so no receiving bank account can be accepted and the Owner gets MSG-DEBT-02, and every bank notification answers 401; `Fake` works in Development only and elsewhere acts as `None`) | `Payments:TimeoutSeconds` (default 15; 1 to 300, anything else stops the host at startup), `Payments:Fake:RejectAccounts`, `Payments:Fake:TimeoutAccounts`, `Payments:Fake:WebhookSecret` (a secret: user-secrets or `Payments__Fake__WebhookSecret`, empty by default, which accepts no notification) |
| File storage (`Storage:Provider` = `Local` or `AzureBlob`; `Storage:Local:SigningKey` is required outside Development; one container or folder per environment, see Background jobs) | `Storage:Local:RootPath`, `Storage:Local:SigningKey`, `Storage:AzureBlob:ConnectionString`, `Storage:AzureBlob:Container` |
| Zalo Mini App "Liên kết tài khoản" (`POST api/auth/zalo/link`, `zalo/login`, `DELETE api/me/zalo-link`; a trader without an account sends `zalo/trader-requests`, which the Cooperative Owner approves or refuses at `api/trader-requests`). `Zalo:Provider` unset by default: linking answers MSG04; `Graph` calls graph.zalo.me and needs the Zalo App secret (developers.zalo.me), the host does not start without it; `Fake` (Development only) accepts `fake:<zalo user id>` access tokens and `fake:<phone number>` phone tokens | `Zalo:Provider`, `Zalo:AppSecret` (user-secrets / `Zalo__AppSecret`), `Zalo:TimeoutSeconds` (default 10) |
| Bootstrap (see Deployment) | `Bootstrap:Cooperative:FullName`, `Bootstrap:Owner:*` |
| Rate limits per client IP (an IPv6 client per /64) and minute | `RateLimits:CodeRequestsPerMinute` (default 10: every endpoint that sends or checks a code), `RateLimits:LoginsPerMinute` (default 30), `RateLimits:ProvenancePagesPerMinute` (default 60: the public provenance page), `RateLimits:PaymentNotificationsPerMinute` (default 600: the bank notification webhook) |
| CORS: the web app and the Zalo Mini App call the API from the browser. Set their origins in every deployed environment; empty means no CORS. Development allows `http://localhost:3000` and `https://h5.zdn.vn`. | `Cors:AllowedOrigins` (array, e.g. `Cors__AllowedOrigins__0=https://<web host>`, `Cors__AllowedOrigins__1=https://h5.zdn.vn`) |
| Provenance link: the QR code of a sales invoice and its SMS (MSG-SALE-15) link to `{PublicBaseUrl}/origin/{qrToken}`, the public page of the web app. **Required** in every environment: without an absolute `http`/`https` URL the host does not start (QR codes are permanent, so a relative link is never made). Development uses `http://localhost:3000`. | `Provenance:PublicBaseUrl` (e.g. `Provenance__PublicBaseUrl=https://<web host>`) |
| Reverse proxy: X-Forwarded-For is trusted only from these addresses (and loopback), so the limits see the real client | `ForwardedHeaders:KnownProxies` (e.g. `10.0.0.4`), `ForwardedHeaders:KnownNetworks` (e.g. `10.0.0.0/24`) |
| Next.js server (BFF) in front of the API: it sends the end user's address and browser so the per-IP limits and the session list see the user, not the BFF. Off by default. Requests carrying `X-Bff-Secret` equal to the shared secret (constant-time compare) may set `X-Client-Ip` and `X-Client-User-Agent`; without a valid secret both are ignored, so an address cannot be chosen by the caller. The secret is **required, at least 32 characters, outside Development** when `Enabled` is true (the host does not start otherwise); in Development an empty secret leaves the feature inactive. The supplied address wins over `X-Forwarded-For`. | `BffClient:Enabled` (default `false`), `BffClient:SharedSecret` (a secret: user-secrets or an environment variable) |
| Refresh grace period: how long the refresh token a rotation replaced still gets an access token (see Authentication) | `Jwt:RefreshGracePeriod` (default `00:00:30`) |
| Revoked-session lookup in Redis instead of SQL on every request (see [Session revocation store](#session-revocation-store-redis)). Optional: unset means SQL. Run Redis with a password, no persistence and `noeviction`; a lock done outside the application must also revoke the account's sessions | `ConnectionStrings:Redis` (a secret), `Revocation:Mode` (`Auto`, `Redis`, `Sql`), `Revocation:CommandTimeout` (default `00:00:00.250`), `Revocation:SweepInterval` (default `00:00:10`, 1 to 30 seconds) |
| Verification codes: SMS sent system-wide per 24 hours; beyond it SMS codes answer MSG-ACCOUNT-13 and a critical log entry is written (BR-ACCOUNT-09: one purpose and one number or address get at most 3 codes per hour, at least 60 seconds apart; over all purposes one number or address gets at most 10) | `Verification:DailySmsLimit` (default 500) |
| Expiry reminder of zone licenses and certifications (GFR-03, daily, business time zone UTC+7) | `Jobs:ZoneLicenseReminder:RunAt` (default `07:00`), `Jobs:ZoneLicenseReminder:CatchUpOnStart` (default `true`: a host started after the run time runs once at start) |
| Overdue water measurement reminder (GFR-04, daily, business time zone UTC+7) | `Jobs:WaterMeasurementOverdue:RunAt` (default `06:00`), `Jobs:WaterMeasurementOverdue:CatchUpOnStart` (default `true`) |
| Daily close check of harvest lots (GFR-05, daily, business time zone UTC+7) | `Jobs:HarvestLotAutoClose:RunAt` (default `00:05`), `Jobs:HarvestLotAutoClose:CatchUpOnStart` (default `true`) |
| Employment end: an employee is Left from the day after the last working day (LABOR-01, daily, business time zone UTC+7) | `Jobs:EmploymentEnd:RunAt` (default `00:01`), `Jobs:EmploymentEnd:CatchUpOnStart` (default `true`) |
| Login session clean-up: sessions that expired or were revoked more than `Retention` ago, with the device key, address and browser they hold (daily, UTC+7) | `Jobs:LoginSessionCleanup:Enabled` (default `true`), `RunAt` (default `03:30`), `CatchUpOnStart` (default `false`), `Retention` (default `30.00:00:00`, at least `1.00:00:00` or the host does not start) |
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
